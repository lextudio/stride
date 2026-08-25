// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Non-Windows twin of the WinForms EmbeddedGameForm: same construction surface the
// EditorGameController/GameStudioPreviewService consume (Handle/Host/Dispose), but there is
// no native window - the game runs through GameContextHeadless and its frames are presented
// by a managed control (HeadlessGameHostView), following OpenDevelop's frame-presenter
// pattern for LibreWPF. The Windows variant lives in EmbeddedGameForm.cs behind
// STRIDE_EDITOR_WINFORMS.

using System;
using System.Windows;
using Stride.Core.Presentation.Controls;

namespace Stride.Editor.Engine
{
    /// <summary>
    /// A <see cref="FrameworkElement"/> presenting the headless editor game's rendered output.
    /// Placeholder slice: renders nothing yet; frame readback/presentation lands with the
    /// GameContextHeadless wiring (see doc/technotes/stride-game-studio.md Phase 3).
    /// </summary>
    public sealed class HeadlessGameHostView : FrameworkElement, IEmbeddedGameHostView
    {
        public FrameworkElement Visual => this;

        public Point PointFromScreen(Point screenPoint) => new(0, 0);

        public void Dispose() { }
    }

    /// <summary>
    /// Headless stand-in for the Windows-only WinForms form: owns no window, exposes the same
    /// members the editor controllers touch.
    /// </summary>
    public sealed class EmbeddedGameForm : IDisposable
    {
        /// <summary>Always <see cref="IntPtr.Zero"/> headlessly; kept so shared code paths compile.</summary>
        public IntPtr Handle => IntPtr.Zero;

        /// <summary>The WPF element hosting this game's presentation.</summary>
        public IEmbeddedGameHostView Host { get; set; }

        public void Dispose() { }
    }
}
