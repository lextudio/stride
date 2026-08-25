// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Platform-neutral seam for embedding a running editor game into the WPF tree.
// On Windows the implementation is GameEngineHost (HWND reparenting of the WinForms
// GameForm's child window); on Linux/macOS the editor runs the game through
// GameContextHeadless and presents frames with a managed control instead - the same
// "frame presenter" pattern OpenDevelop's designers proved out under LibreWPF.

using System;
using System.Windows;

namespace Stride.Core.Presentation.Controls
{
    /// <summary>
    /// The WPF-facing half of an embedded editor game: the element the asset views insert
    /// into their content, plus the screen-to-local mapping the controllers need.
    /// </summary>
    public interface IEmbeddedGameHostView : IDisposable
    {
        /// <summary>The element to insert into the visual tree.</summary>
        FrameworkElement Visual { get; }

        /// <summary>Maps a point in screen coordinates to this view's local coordinates.</summary>
        Point PointFromScreen(Point screenPoint);
    }
}
