// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Headless twin of EditorGameController.DragDrop.cs: keeps the members the rest of the
// editor calls (DoDragDrop, DisableDrop/EnableDrop) available on non-Windows hosts where
// the WinForms OLE implementation is compiled out. Real drag-and-drop for the headless
// presentation lands with the frame-presenter input slice (WPF-routed drops forwarded to
// the game thread) - see doc/technotes/stride-game-studio.md.

using System;
using System.Runtime.CompilerServices;

namespace Stride.Assets.Presentation.AssetEditors.GameEditor.Services
{
    partial class EditorGameController<TEditorGame>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void DisableDrop()
        {
            // No native-window drop target to disable headlessly.
        }

        /// <summary>
        /// Initiates a drag-and-drop operation. Headless no-op: reports "no effect" instead
        /// of performing an OLE drag.
        /// </summary>
        /// <param name="items">The items to drag.</param>
        /// <returns><see cref="System.Windows.DragDropEffects.None"/></returns>
        protected internal System.Windows.DragDropEffects DoDragDrop(params object[] items)
        {
            return System.Windows.DragDropEffects.None;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void EnableDrop()
        {
            // No native-window drop target to enable headlessly.
        }

        // RegisterToDragDropEvents/UnregisterFromDragDropEvents: declared in
        // EditorGameController.cs; with no defining implementation here the partial methods
        // are simply omitted (all call sites no-op) on non-Windows hosts.
    }
}
