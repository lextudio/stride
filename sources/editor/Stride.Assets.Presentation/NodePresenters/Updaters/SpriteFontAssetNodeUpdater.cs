// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Stride.Core.Assets.Editor.Quantum.NodePresenters;
using Stride.Assets.Presentation.NodePresenters.Keys;
using Stride.Assets.SpriteFont;

namespace Stride.Assets.Presentation.NodePresenters.Updaters
{
    internal sealed class SpriteFontAssetNodeUpdater : AssetNodePresenterUpdaterBase
    {
        private static readonly IEnumerable<string> InstalledFonts;

        static SpriteFontAssetNodeUpdater()
        {
            // The installed-font list only populates a suggestion dropdown on the sprite font asset,
            // so losing it costs a convenience, not a capability - and it is the single thing in this
            // assembly that needs System.Drawing.Common. That dependency is unavailable or unusable
            // outside Windows: the implementation ships in the Windows-only Microsoft.WindowsDesktop.App
            // runtime pack, and System.Drawing.Common's APIs throw PlatformNotSupportedException on
            // non-Windows since .NET 6. A host that substitutes its own assembly of the same simple
            // name (LibreWPF does, for the OpenDevelop fusion) makes it fail to bind as well. Degrade
            // to an empty list instead of letting a static ctor failure take down every consumer of
            // this updater - a TypeInitializationException here aborts the whole session load.
            try
            {
                InstalledFonts = QueryInstalledFonts();
            }
            catch (Exception)
            {
                InstalledFonts = [];
            }
        }

        // Kept out of the static ctor so that merely running the ctor does not require resolving
        // System.Drawing.Common: the JIT loads a method's referenced assemblies when it compiles that
        // method, so the reference must live in a method that is only entered inside the try above.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string[] QueryInstalledFonts()
        {
            var installedFontCollection = new System.Drawing.Text.InstalledFontCollection();
            return installedFontCollection.Families.Select(x => x.Name).ToArray();
        }

        protected override void UpdateNode(IAssetNodePresenter node)
        {
            // Root node
            if (typeof(SpriteFontAsset).IsAssignableFrom(node.Type))
            {
                node.AttachedProperties.Add(SpriteFontData.Key, InstalledFonts);
            }
        }
    }
}
