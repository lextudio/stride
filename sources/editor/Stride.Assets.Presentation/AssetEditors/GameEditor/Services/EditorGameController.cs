// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
#if STRIDE_EDITOR_WINFORMS
using System.Windows.Forms;
#endif
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Assets.Quantum;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Controls;
using Stride.Core.Presentation.Services;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Assets.Presentation.AssetEditors.GameEditor.ViewModels;
using Stride.Editor.Build;
using Stride.Editor.EditorGame.ContentLoader;
using Stride.Editor.EditorGame.Game;
using Stride.Editor.EditorGame.ViewModels;
using Stride.Editor.Engine;
using Stride.Engine.Processors;
using Stride.Games;
using Stride.Shaders.Compiler;
#if !STRIDE_EDITOR_WINFORMS
using Stride.Graphics.SDL;
#endif
using Point = System.Windows.Point;

namespace Stride.Assets.Presentation.AssetEditors.GameEditor.Services
{
    /// <summary>
    /// Represents a factory to create the <see cref="EditorServiceGame"/>.
    /// </summary>
    /// <typeparam name="TEditorGame"></typeparam>
    /// <param name="gameContentLoadedTaskSource"></param>
    /// <param name="effectCompiler"></param>
    /// <param name="effectLogPath"></param>
    /// <returns></returns>
    public delegate TEditorGame EditorGameFactory<out TEditorGame>(TaskCompletionSource<bool> gameContentLoadedTaskSource, IEffectCompiler effectCompiler, string effectLogPath)
        where TEditorGame : EditorServiceGame;

    /// <summary>Non-generic internal hook so external assemblies (test harness) can reach the
    /// underlying <see cref="EditorServiceGame"/> without depending on the closed generic type.</summary>
    internal interface IEditorGameAccess
    {
        EditorServiceGame EditorGame { get; }
    }

    public abstract partial class EditorGameController<TEditorGame> : IEditorGameController, IEditorGameAccess
        where TEditorGame : EditorServiceGame
    {
        /// <summary>
        /// The scene asset view model associated to this scene service.
        /// </summary>
        protected readonly AssetViewModel Asset;
        /// <summary>
        /// Gets the game associated with the scene editor.
        /// </summary>
        protected readonly TEditorGame Game;

        EditorServiceGame IEditorGameAccess.EditorGame => Game;
        /// <summary>
        /// The scene game thread. On Windows this is a dedicated background thread the game
        /// loop runs on (see <see cref="SceneGameRunThread"/>). On macOS/Linux this is instead
        /// the thread that calls <see cref="StartGame"/> - captured lazily there, not created
        /// here - because SDL/Cocoa window creation and event pumping must happen on the
        /// process's real main thread, and this class's own game-thread-identity checks
        /// (<see cref="IsInGameThread"/>, <see cref="EnsureGameAccess"/>) need SOME stable
        /// <see cref="Thread"/> reference to compare against either way. See
        /// doc/technotes/stride-game-studio.md (OpenDevelop fusion) for the fuller rationale.
        /// </summary>
        private Thread sceneGameThread;

#if !STRIDE_EDITOR_WINFORMS
        /// <summary>
        /// The SDL window the game renders into on macOS/Linux, exposed so an embedding host
        /// (e.g. the OpenDevelop addin) can attach its own native composition bridge - see
        /// StrideSdlViewport.cs's `addChildWindow` overlay technique in the OpenDevelop repo.
        /// This class deliberately does not know about Cocoa/WPF-hosting specifics itself.
        /// </summary>
        private Window sdlWindow;
        private GameContextSDL sdlContext;

        /// <summary>
        /// Typed as <c>object</c> to match <see cref="IEditorGameController.SdlWindow"/> (that
        /// interface stays platform-neutral, with no <c>Stride.Graphics.SDL</c> reference of its
        /// own) - callers cast back to <see cref="Window"/>.
        /// </summary>
        public object SdlWindow => sdlWindow;

        /// <summary>
        /// Pumps one frame of the game loop. Must be called from the same thread that called
        /// <see cref="StartGame"/> (the embedding host's UI/main thread) - typically driven by
        /// that host's own per-frame tick (e.g. WPF's CompositionTarget.Rendering), mirroring
        /// StrideSdlViewport's own OnRendering handler.
        /// </summary>
        public void Tick()
        {
            // Not re-entrant: on Windows, pumping SDL events (here and inside the frame) also dispatches the host's own window messages on this
            // thread, including the host's next render tick, which would call Tick again in the middle of a frame. That nested frame applied the
            // pending resize (destroying images the outer frame's command list references) and reset the shared command list, so the outer frame
            // went on recording into an invalid Vulkan command buffer (validation: "VkImage ... was destroyed"; Lavapipe: access violation).
            if (sdlContext == null || ticking)
                return;
            ticking = true;
            try
            {
                Stride.Graphics.SDL.Application.ProcessEvents();
                sdlContext.RunCallback?.Invoke();
            }
            finally
            {
                ticking = false;
            }
        }

        private bool ticking;

        /// <inheritdoc/>
        public void ResizeGame(int width, int height)
        {
            // Drive the size through the PRESENTER at exactly this size (not GraphicsDeviceManager.ApplyChanges, which never updates a live
            // swapchain; and bypassing the macOS SDL drawable's polluted currentExtent that would otherwise squash the frame). Applied by the
            // game at the start of its next frame, not here: this is called from the host's UI events, which can arrive in the middle of a frame
            // (see EditorServiceGame.RequestBackBufferResize).
            Game?.RequestBackBufferResize(width, height);
        }
#endif
        /// <summary>
        /// The service registry.
        /// </summary>
        private EditorGameServiceRegistry serviceRegistry;
        /// <summary>
        /// The debug page that displays the scene log.
        /// </summary>
        private readonly IDebugPage debugPage;
        /// <summary>
        /// The handle of the game form.
        /// </summary>
        private IntPtr windowHandle;
#if STRIDE_EDITOR_WINFORMS
        /// <summary>
        /// The last click position.
        /// </summary>
        private System.Drawing.Point lastClickPosition;
#endif
        /// <summary>
        /// Task completion source that sets the result when the game is started.
        /// </summary>
        private readonly TaskCompletionSource<bool> gameStartedTaskSource = new TaskCompletionSource<bool>();
        /// <summary>
        /// Task completion source that sets the result when the game content is loaded.
        /// </summary>
        private readonly TaskCompletionSource<bool> gameContentLoadedTaskSource = new TaskCompletionSource<bool>();

        /// <summary>
        /// Initializes a new instance of the <see cref="EditorGameController{TEditorGame}"/> class.
        /// </summary>
        /// <param name="asset">The asset associated with this instance.</param>
        /// <param name="editor">The editor associated with this instance.</param>
        /// <param name="gameFactory">The factory to create the editor game.</param>
        protected EditorGameController([NotNull] AssetViewModel asset, [NotNull] GameEditorViewModel editor, [NotNull] EditorGameFactory<TEditorGame> gameFactory)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (editor == null) throw new ArgumentNullException(nameof(editor));

            Asset = asset;
            Editor = editor;
            GameSideNodeContainer = new SessionNodeContainer(asset.Session) { NodeBuilder = { NodeFactory = new AssetNodeFactory() } };

            //Logger = GlobalLogger.GetLogger("Scene");
            Logger = new LoggerResult();
            debugPage = EditorDebugTools.CreateLogDebugPage(Logger, "Scene");

            // Create the game
            var builderService = asset.ServiceProvider.Get<GameStudioBuilderService>();
            Game = gameFactory(gameContentLoadedTaskSource, builderService.EffectCompiler, builderService.EffectLogPath);
            Game.PackageSettings = asset.ServiceProvider.Get<GameSettingsProviderService>();
#if STRIDE_EDITOR_WINFORMS
            sceneGameThread = new Thread(SafeAction.Wrap(SceneGameRunThread)) { IsBackground = true, Name = $"EditorGameThread ({asset.Url})" };
            // STA is a Windows-COM concept; requesting it elsewhere throws PlatformNotSupportedException
            // (LibreWPF does not need it - same gating OpenDevelop's designer child hosts use).
            sceneGameThread.SetApartmentState(ApartmentState.STA);
#else
            // No dedicated thread here - SDL/Cocoa needs the real process main thread, which
            // this constructor does not run on in general. StartGame() captures whichever
            // thread calls it instead (see the sceneGameThread field comment).
#endif

            Debug = new EditorGameDebugService();
            Loader = new EditorContentLoader(this, Logger, asset, editor, Game);
        }

        /// <inheritdoc/>
        public IEditorContentLoader Loader { get; }

        /// <inheritdoc/>
        public Logger Logger { get; }

        public EditorGameDebugService Debug { get; }

        /// <inheritdoc/>
        public SessionNodeContainer GameSideNodeContainer { get; }

        /// <summary>
        /// The WPF element the asset views insert to display the game (platform-neutral seam:
        /// GameEngineHost on Windows, HeadlessGameHostView elsewhere).
        /// </summary>
        public IEmbeddedGameHostView EditorHost => GameForm?.Host;

        /// <inheritdoc/>
        public Task GameContentLoaded => gameContentLoadedTaskSource.Task;

        protected GameEditorViewModel Editor { get; }

        /// <summary>
        /// The game form hosting the scene view.
        /// </summary>
        protected EmbeddedGameForm GameForm { get; private set; }

        protected EditorGameRecoveryService RecoveryService { get; private set; }

        /// <summary>
        /// Indicates whether this controller has been destroyed.
        /// </summary>
        /// <remarks>
        /// Once this property is <c>true</c>, attempting to call a method on the controller will throw a <see cref="ObjectDisposedException"/>.
        /// </remarks>
        /// <seealso cref="IsDestroying"/>
        protected bool IsDestroyed { get; private set; }

        /// <summary>
        /// Indicates whether this controller is currently being destroyed.
        /// </summary>
        /// <remarks>
        /// While this property is <c>true</c>, attempting to call a method on the controller will not throw but might
        /// either return a default value (e.g. <see cref="GetService{T}"/>) or a cancelled task (e.g. <see cref="InvokeAsync"/>.
        /// </remarks>
        /// <seealso cref="IsDestroyed"/>
        protected bool IsDestroying { get; private set; }

        /// <inheritdoc/>
        /// <remarks>
        /// Derived class should override this method, implement specific cleanup and then call the base implementation.
        /// </remarks>
        public virtual void Destroy()
        {
            EnsureNotDestroyed();
            if (IsDestroying)
                return;

            Loader.Dispose();
            EditorDebugTools.UnregisterDebugPage(debugPage);
            UnregisterFromDragDropEvents();
            GameForm?.Host?.Dispose();
            IsDestroying = true;

            // Clean after everything
            PostTask(async x =>
            {
                if (serviceRegistry != null)
                    await serviceRegistry.DisposeAsync();
                Game.Exit();
                GameForm?.Dispose();
            }, int.MaxValue)
            // Ensure the properties are correctly set, even in case of a failure (default continuation)
            .ContinueWith(t =>
            {
                Asset.Dispatcher.Invoke(() =>
                {
                    IsDestroyed = true;
                    IsDestroying = false;
                });
            });
            GameSideNodeContainer.Clear();
        }

        /// <inheritdoc/>
        public abstract Task<bool> CreateScene();

        /// <inheritdoc/>
        public object FindGameSidePart(AbsoluteId partId)
        {
            EnsureNotDestroyed();
            EnsureGameAccess();
            return FindPart(partId);
        }

        [CanBeNull]
        protected abstract object FindPart(AbsoluteId partId);

        public T GetService<T>() where T : IEditorGameViewModelService
        {
            EnsureNotDestroyed();
            EnsureAssetAccess();
            if (IsDestroying || serviceRegistry == null)
                return default(T);
            return serviceRegistry.Get<T>();
        }

        /// <inheritdoc/>
        public async Task<bool> StartGame()
        {
            EnsureNotDestroyed();
            if (IsDestroying)
                throw new InvalidOperationException("This controller is beeing disposed.");
#if STRIDE_EDITOR_WINFORMS
            // Run the game in a separate thread (create Form and run)
            sceneGameThread.Start();
            // Wait for the game to start
            await gameStartedTaskSource.Task;
            GameForm.MouseDown += (sender, e) => lastClickPosition = Control.MousePosition;
            // Initialize the WPF GameEngineHwndHost on this thread
            GameForm.Host = new GameEngineHost(windowHandle);
#else
            // SDL/Cocoa needs the process's real main thread - run setup on whichever thread
            // called StartGame() (the embedding host is expected to call this from its own
            // UI/main thread) instead of spawning a dedicated background thread. The
            // (potentially slow) shader wait happens off that thread first so it doesn't block
            // the caller's UI.
            await Task.Run(() => Asset.ServiceProvider.Get<GameStudioBuilderService>().WaitForShaders());
            sceneGameThread = Thread.CurrentThread;

            GameForm = new EmbeddedGameForm();
            sdlWindow = new Window("EditorGameController scene", IntPtr.Zero);
            // InitializeDatabase = false, matching the WinForms context this branch replaces: the
            // editor supplies its own micro-thread-aware IDatabaseFileProviderService, but
            // Game.PrepareContext hard-casts that service to the concrete DatabaseFileProviderService
            // when it initializes the database itself, which throws against the editor's
            // MicroThreadLocalProviderService.
            sdlContext = new GameContextSDL(sdlWindow, 0, 0, isUserManagingRun: true) { InitializeDatabase = false };

            serviceRegistry = new EditorGameServiceRegistry();
            InitializeServices(serviceRegistry);
            Game.RegisterServices(serviceRegistry);

            Game.Run(sdlContext); // returns immediately: isUserManagingRun defers the loop to Tick()
            gameStartedTaskSource.SetResult(true);

            // No native-window-embedding seam is wired up here on purpose: unlike the WinForms
            // path (GameEngineHost) or the old headless path (HeadlessGameHostView), the actual
            // Cocoa `addChildWindow` composition bridge lives entirely in the embedding host
            // (see StrideSdlViewport.cs in the OpenDevelop addin) - this class only exposes
            // SdlWindow and Tick() for that host to drive.
            GameForm.Host = new HeadlessGameHostView();
#endif

            // TODO: we could check if the game fails to create.
            return true;
        }

        public void OnHideGame()
        {
            Game.IsEditorHidden = true;
        }

        public void OnShowGame()
        {
            Game.IsEditorHidden = false;
        }

        public Vector3 GetMousePositionInScene(bool lastRightClick)
        {
            EnsureNotDestroyed();
            if (IsDestroying)
                return Vector3.Zero;
#if STRIDE_EDITOR_WINFORMS
            var mousePosition = lastRightClick ? lastClickPosition : Control.MousePosition;
            var localPosition = GameForm.Host.PointFromScreen(new Point(mousePosition.X, mousePosition.Y));
            var relativePos = new Vector2((float)(localPosition.X / GameForm.Host.Visual.ActualWidth), (float)(localPosition.Y / GameForm.Host.Visual.ActualHeight));
            return Game.GetPositionInScene(relativePos);
#else
            // Headless presentation has no global-screen mapping yet (frame presenter slice);
            // report the scene origin rather than guessing a screen-space position.
            return Vector3.Zero;
#endif
        }

        public void TriggerActiveRenderStageReevaluation()
        {
            EnsureNotDestroyed();
            if (IsDestroying)
                return;
            Game.TriggerActiveRenderStageReevaluation();
        }

        public void ChangeCursor(System.Windows.Input.Cursor cursor)
        {
            EnsureNotDestroyed();
            if (IsDestroying)
                return;
#if STRIDE_EDITOR_WINFORMS
            GameForm.Cursor = ToFormsCursor(cursor);
#endif
            // Headless: cursor shapes land with the frame-presenter input slice.
        }

#if STRIDE_EDITOR_WINFORMS
        private static System.Windows.Forms.Cursor ToFormsCursor(System.Windows.Input.Cursor cursor)
        {
            // The adorner layer speaks WPF cursors; the embedded WinForms form wants its own.
            if (cursor == System.Windows.Input.Cursors.Arrow) return System.Windows.Forms.Cursors.Arrow;
            if (cursor == System.Windows.Input.Cursors.No) return System.Windows.Forms.Cursors.No;
            if (cursor == System.Windows.Input.Cursors.SizeAll) return System.Windows.Forms.Cursors.SizeAll;
            if (cursor == System.Windows.Input.Cursors.SizeNS) return System.Windows.Forms.Cursors.SizeNS;
            if (cursor == System.Windows.Input.Cursors.SizeWE) return System.Windows.Forms.Cursors.SizeWE;
            if (cursor == System.Windows.Input.Cursors.SizeNWSE) return System.Windows.Forms.Cursors.SizeNWSE;
            if (cursor == System.Windows.Input.Cursors.SizeNESW) return System.Windows.Forms.Cursors.SizeNESW;
            return System.Windows.Forms.Cursors.Default;
        }
#endif

        /// <inheritdoc/>
        public Task InvokeAsync(Action callback, CancellationToken token = default)
        {
            EnsureNotDestroyed();
            if (IsDestroying)
                return Task.FromCanceled(new CancellationToken(true));
            return PostActionAsync(callback, 0);
        }

        /// <inheritdoc/>
        public Task LowPriorityInvokeAsync(Action callback, CancellationToken token = default)
        {
            EnsureNotDestroyed();
            if (IsDestroying)
                return Task.FromCanceled(new CancellationToken(true));
            return PostActionAsync(callback, int.MaxValue);
        }

        /// <inheritdoc/>
        public Task<TResult> InvokeAsync<TResult>(Func<TResult> callback, CancellationToken token = default)
        {
            EnsureNotDestroyed();
            if (IsDestroying)
                return Task.FromCanceled<TResult>(new CancellationToken(true));
            return PostActionAsync(callback, 0);
        }

        /// <inheritdoc/>
        public Task InvokeTask(Func<Task> task, CancellationToken token = default)
        {
            EnsureNotDestroyed();
            if (IsDestroying)
                return Task.FromCanceled(new CancellationToken(true));
            return PostTask(x => task(), 0);
        }

        /// <inheritdoc/>
        public Task<TResult> InvokeTask<TResult>(Func<Task<TResult>> task, CancellationToken token = default)
        {
            EnsureNotDestroyed();
            if (IsDestroying)
                return Task.FromCanceled<TResult>(new CancellationToken(true));
            return PostTask(x => task(), 0);
        }

        /// <summary>
        /// Verifies that the current thread is the main thread.
        /// </summary>
        /// <returns><c>True</c> if the current thread is the main thread, <c>False</c> otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CheckAssetAccess()
        {
            return Asset.Dispatcher.CheckAccess();
        }

        /// <summary>
        /// Ensures that the current thread is the main thread. This method will throw an exception if it is not the case.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureAssetAccess()
        {
            Asset.Dispatcher.EnsureAccess();
        }

        /// <summary>
        /// Verifies that the current thread is the game thread.
        /// </summary>
        /// <returns><c>True</c> if the current thread is the game thread, <c>False</c> otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CheckGameAccess()
        {
            return Thread.CurrentThread == sceneGameThread;
        }

        /// <summary>
        /// Ensures that the current thread is the game thread. This method will throw an exception if it is not the case.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureGameAccess(bool inGameThread = true)
        {
            if (inGameThread && Thread.CurrentThread != sceneGameThread)
                throw new InvalidOperationException("This code must be executed in the game thread.");
            if (!inGameThread && Thread.CurrentThread == sceneGameThread)
                throw new InvalidOperationException("This code must not be executed in the game thread.");
        }

        /// <inheritdoc/>
        bool IDispatcherService.CheckAccess() => CheckGameAccess();

        /// <inheritdoc/>
        void IDispatcherService.EnsureAccess(bool inDispatcherThread) => EnsureGameAccess(inDispatcherThread);

        /// <inheritdoc/>
        void IDispatcherService.Invoke(Action callback)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        TResult IDispatcherService.Invoke<TResult>(Func<TResult> callback)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Checks whether this controller has been disposed, and throws an <see cref="ObjectDisposedException"/> if it is the case.
        /// </summary>
        /// <param name="name">The name to supply to the <see cref="ObjectDisposedException"/>.</param>
        protected void EnsureNotDestroyed(string name = null)
        {
            if (IsDestroyed)
            {
                throw new ObjectDisposedException(name ?? nameof(EditorGameController<TEditorGame>), "This controller has already been disposed.");
            }
        }

        protected virtual void InitializeServices([NotNull] EditorGameServiceRegistry services)
        {
            services.Add(new EditorGameDebugService());
            services.Add(RecoveryService = new EditorGameRecoveryService(Editor) { IsActive = true });
        }

#if STRIDE_EDITOR_WINFORMS
        // Only ever invoked via sceneGameThread.Start() from StartGame()'s WinForms branch - the
        // macOS/Linux path runs its own setup inline in StartGame() instead (SDL/Cocoa needs the
        // caller's thread to BE the process main thread, so there is no separate "run thread" to
        // start there - see the sceneGameThread field comment).
        private void SceneGameRunThread()
        {
            // Create the form from this thread
            GameForm = new EmbeddedGameForm
            {
                TopLevel = false,
                Visible = false,
            };
            windowHandle = GameForm.Handle;
            var context = new GameContextWinforms(GameForm) { InitializeDatabase = false };
            RegisterToDragDropEvents();

            // Wait for shaders to be loaded
            Asset.ServiceProvider.Get<GameStudioBuilderService>().WaitForShaders();

            // Create and register services
            serviceRegistry = new EditorGameServiceRegistry();
            InitializeServices(serviceRegistry);
            Game.RegisterServices(serviceRegistry);

            // Notify game start
            gameStartedTaskSource.SetResult(true);
            Game.Run(context);
            Game.Dispose();
        }
#endif

        partial void RegisterToDragDropEvents();

        partial void UnregisterFromDragDropEvents();

        private Task PostTask(Func<ScriptSystem, Task> task, int priority)
        {
            var tcs = new TaskCompletionSource<int>();
            Game.Script.AddTask(async () => { await task(Game.Script); tcs.SetResult(0); }, priority);
            return tcs.Task;
        }

        private Task<T> PostTask<T>(Func<ScriptSystem, Task<T>> task, int priority)
        {
            var tcs = new TaskCompletionSource<T>();
            Game.Script.AddTask(async () => { var result = await task(Game.Script); tcs.SetResult(result); }, priority);
            return tcs.Task;
        }

        private Task PostActionAsync(Action action, int priority)
        {
            if (CheckGameAccess())
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<int>();
            Game.Script.AddTask(() => { action(); tcs.SetResult(0); return tcs.Task; }, priority);
            return tcs.Task;
        }

        private Task<T> PostActionAsync<T>(Func<T> action, int priority)
        {
            if (CheckGameAccess())
            {
                var result = action();
                return Task.FromResult(result);
            }

            var tcs = new TaskCompletionSource<T>();
            Game.Script.AddTask(() => { var result = action(); tcs.SetResult(result); return tcs.Task; }, priority);
            return tcs.Task;
        }
    }
}
