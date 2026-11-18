// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
//
// Windows-only WinForms variant of the embedded editor game form. The non-Windows twin
// (same member surface, headless, no native window) lives in EmbeddedGameForm.Headless.cs;
// the switch is STRIDE_EDITOR_WINFORMS, set per host OS in sources/Directory.Build.props.
#if STRIDE_EDITOR_WINFORMS
using Stride.Core.Presentation.Controls;
using Stride.Core.Presentation.Interop;
using Stride.Games;
using System.ComponentModel;

namespace Stride.Editor.Engine
{
    /// <summary>
    /// A specialization of <see cref="GameForm"/> that is able to forward keyboard and mousewheel events to an associated <see cref="GameEngineHost"/>.
    /// </summary>
    [System.ComponentModel.DesignerCategory("")]
    public class EmbeddedGameForm : GameForm
    {
        public EmbeddedGameForm()
        {
            enableFullscreenToggle = false;
        }

        /// <summary>
        /// Gets or sets the embedded-game view associated to this form.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public IEmbeddedGameHostView Host { get; set; }

        /// <inheritdoc/>
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (Host != null)
            {
                switch (m.Msg)
                {
                    case NativeHelper.WM_KEYDOWN:
                    case NativeHelper.WM_KEYUP:
                    case NativeHelper.WM_MOUSEWHEEL:
                    case NativeHelper.WM_RBUTTONDOWN:
                    case NativeHelper.WM_RBUTTONUP:
                    case NativeHelper.WM_LBUTTONDOWN:
                    case NativeHelper.WM_LBUTTONUP:
                    case NativeHelper.WM_MOUSEMOVE:
                    case NativeHelper.WM_CONTEXTMENU:
                        // Host is the headless-capable interface in this fork; only the Windows host (GameEngineHost) owns a window to forward to.
                        (Host as GameEngineHost)?.ForwardMessage(m.HWnd, m.Msg, m.WParam, m.LParam);
                        break;
                }
            }
            base.WndProc(ref m);
        }
    }
}
#endif
