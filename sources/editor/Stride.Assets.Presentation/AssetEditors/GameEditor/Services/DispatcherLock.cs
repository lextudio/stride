// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stride.Core.Annotations;
using Stride.Core.Extensions;
using Stride.Core.Presentation.Services;

namespace Stride.Assets.Presentation.AssetEditors.GameEditor.Services
{
    /// <summary>
    /// An object that allows to lock several dispatchers at the same-time to perform an operation in a thread-safe way.
    /// </summary>
    public class DispatcherLock : IDisposable
    {
        // TODO: we might want to move that to the plugin level at some point (or even above? in Presentation?)
        private sealed class DispatcherState
        {
            public DispatcherState([NotNull] IDispatcherService dispatcher)
            {
                if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
                if (dispatcher.CheckAccess()) throw new InvalidOperationException("A dispatcher lock must be created from a different thread that the dispatchers it should lock");
                Dispatcher = dispatcher;
                Locked = new TaskCompletionSource<int>();
                Unlocked = new TaskCompletionSource<int>();
            }

            public readonly IDispatcherService Dispatcher;
            public readonly TaskCompletionSource<int> Locked;
            public readonly TaskCompletionSource<int> Unlocked;

            /// <summary>
            /// True if this dispatcher was not locked because an earlier dispatcher in the same lock turned out to be
            /// backed by the same thread, which already excludes itself.
            /// </summary>
            public bool Skipped;
        }

        private readonly List<DispatcherState> dispatcherStates;

        /// <summary>
        /// Initializes a new instance of the <see cref="DispatcherLock"/> class.
        /// </summary>
        /// <param name="dispatchers">The dispatchers to lock.</param>
        private DispatcherLock([ItemNotNull, NotNull] IEnumerable<IDispatcherService> dispatchers)
        {
            dispatcherStates = dispatchers.Select(x => new DispatcherState(x)).ToList();
        }

        /// <summary>
        /// Disposes this lock. This will unlock all dispatchers.
        /// </summary>
        public void Dispose()
        {
            // Unlock all dispatchers.
            foreach (var dispatcher in dispatcherStates)
            {
                if (dispatcher.Skipped)
                    continue;

                dispatcher.Unlocked.SetResult(0);
            }
        }

        /// <summary>
        /// Creates a new instance of the <see cref="DispatcherLock"/> class.
        /// </summary>
        /// <param name="dispatchers">The dispatchers to lock.</param>
        /// <returns>The newly created instance of the <see cref="DispatcherLock"/> class.</returns>
        /// <remarks>
        /// Dispatchers are unlocked when the returned <see cref="DispatcherLock"/> is disposed.
        /// </remarks>
        [ItemNotNull, NotNull]
        public static Task<DispatcherLock> Lock([ItemNotNull, NotNull] params IDispatcherService[] dispatchers)
        {
            return Lock(false, dispatchers);
        }

        /// <summary>
        /// Creates a new instance of the <see cref="DispatcherLock"/> class.
        /// </summary>
        /// <param name="dispatchers">The dispatchers to lock.</param>
        /// <param name="lockSequencially">If true, dispatchers will be locked sequencially, in the order they are enumerated. This allows to lock critical dispatchers in last.</param>
        /// <returns>The newly created instance of the <see cref="DispatcherLock"/> class.</returns>
        /// <remarks>
        /// Dispatchers are unlocked when the returned <see cref="DispatcherLock"/> is disposed.
        /// </remarks>
        /// <remarks>
        /// If several of the given dispatchers turn out to be backed by the same thread, only the first of them is
        /// actually locked - that thread already excludes itself, and blocking it a second time would deadlock. This
        /// can only be detected when <paramref name="lockSequencially"/> is true.
        /// </remarks>
        [ItemNotNull, NotNull]
        public static async Task<DispatcherLock> Lock(bool lockSequencially, [ItemNotNull, NotNull]  params IDispatcherService[] dispatchers)
        {
            if (dispatchers == null) throw new ArgumentNullException(nameof(dispatchers));

            var lok = new DispatcherLock(dispatchers);
            for (var i = 0; i < lok.dispatcherStates.Count; ++i)
            {
                var dispatcher = lok.dispatcherStates[i];

                // Skipped by an earlier dispatcher that turned out to run on the same thread - see below.
                if (dispatcher.Skipped)
                    continue;

                var index = i;
                dispatcher.Dispatcher.InvokeAsync(() =>
                {
                    // Any dispatcher we have not locked yet that considers *this* thread its own is backed by the same
                    // thread as this one. Locking it too would deadlock, since this thread is about to block below and
                    // could never run its lock closure - and it would be pointless anyway, as a single thread already
                    // excludes itself. This check has to happen here because CheckAccess() is only meaningful when
                    // evaluated on the thread in question.
                    for (var j = index + 1; j < lok.dispatcherStates.Count; ++j)
                    {
                        var other = lok.dispatcherStates[j];
                        if (other.Dispatcher.CheckAccess())
                            other.Skipped = true;
                    }

                    // This dispatcher is now locked...
                    dispatcher.Locked.SetResult(0);
                    // ... and blocked until it's unlocked
                    dispatcher.Unlocked.Task.Wait();
                }).Forget();

                if (lockSequencially)
                {
                    // Wait for this dispatcher to be locked before proceeding, if sequencial locking was requested.
                    await dispatcher.Locked.Task;
                }
            }

            // Wait for all the dispatcher to be locked.
            await Task.WhenAll(lok.dispatcherStates.Where(x => !x.Skipped).Select(x => x.Locked.Task).Cast<Task>());
            return lok;
        }
    }
}
