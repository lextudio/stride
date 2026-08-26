// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Threading.Tasks;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Diagnostics;
using Stride.Core.Presentation.Services;
using Stride.Editor.EditorGame.ContentLoader;
using Stride.Editor.EditorGame.ViewModels;

namespace Stride.Assets.Presentation.AssetEditors.GameEditor.Services
{
    public interface IEditorGameController : IDestroyable, IDispatcherService
    {
        [NotNull]
        IEditorContentLoader Loader { get; }

        [NotNull]
        Logger Logger { get; }

        [NotNull]
        Task GameContentLoaded { get; }

        /// <summary>
        /// The <see cref="SessionNodeContainer"/> to use to create Quantum graphs for game side elements.
        /// </summary>
        /// <remarks>
        /// We use another node container so that nodes created for the game editor can be discarded once we close it.
        /// </remarks>
        [NotNull]
        SessionNodeContainer GameSideNodeContainer { get; }

        /// <summary>
        /// Starts and initialize the game thread.
        /// </summary>
        /// <returns>True if the game was successfully initialized, false otherwise.</returns>
        Task<bool> StartGame();

        /// <summary>
        /// Creates the scene for the related editor game.
        /// </summary>
        /// <returns>True if the scene was successfully created, false otherwise.</returns>
        Task<bool> CreateScene();

        /// <summary>
        /// Stops the game from rendering and throttle game updates.
        /// </summary>
        void OnHideGame();

        /// <summary>
        /// Resumes the game rendering and updating and .
        /// </summary>
        void OnShowGame();

        /// <summary>
        /// Finds the game-side instance corresponding to the part with the given id, if it exists.
        /// </summary>
        /// <param name="partId">The id of the part to look for.</param>
        /// <returns>The game-side instance corresponding to the given id if it exists, or null.</returns>
        /// <remarks>This method must be called from the game thread.</remarks>
        [CanBeNull]
        object FindGameSidePart(AbsoluteId partId);

        [CanBeNull]
        T GetService<T>() where T : IEditorGameViewModelService;

        /// <summary>
        /// Triggers a re-evaluation of the active render stages.
        /// </summary>
        void TriggerActiveRenderStageReevaluation();

        /// <summary>
        /// The SDL window the game renders into on macOS/Linux (null on Windows, where
        /// <see cref="GameContextWinforms"/>/<see cref="Stride.Editor.EditorGame.Game.GameEngineHost"/>
        /// is used instead). Exposed so an embedding host can attach its own native composition
        /// bridge - see StrideSdlViewport.cs's `addChildWindow` overlay technique in the
        /// OpenDevelop fusion addin (doc/technotes/stride-game-studio.md).
        /// </summary>
        object SdlWindow => null;

        /// <summary>
        /// Pumps one frame of the game loop on macOS/Linux, where the game does not run its own
        /// dedicated thread (SDL/Cocoa needs the real process main thread instead - see
        /// <see cref="StartGame"/>'s implementation notes). No-op on Windows. Must be called from
        /// the same thread that called <see cref="StartGame"/>.
        /// </summary>
        void Tick() { }
    }
}
