// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Templates;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Assets.Templates;

/// <summary>
/// Bootstraps the editor-internal template registry (asset / script / project-modification .sdtpl
/// files in Stride.Assets.Presentation + Stride.SpriteStudio.Offline, plus the dotnet new project
/// templates from Stride.Templates.*). Non-WPF entry point — callable from headless test runners
/// without pulling Stride.Assets.Presentation (and its WPF dependency tree) into the consumer.
/// </summary>
public static class StrideDefaultTemplates
{
    /// <summary>
    /// Loads default templates. Pass <paramref name="loadAssemblyReferences"/>=false in headless
    /// test runners so the .sdpkg's referenced assemblies (Stride.Particles etc.) don't get loaded
    /// from .nuget cache — a later sample regen loads them from the sample's bin/ via a different
    /// path, producing duplicate Assembly objects and a DataContract alias conflict at runtime.
    /// </summary>
    public static void Load(bool loadAssemblyReferences = true)
    {
        // Only TemplateFolders are needed here (loaded regardless of this flag); skip the package's
        // assets — they're unused for template registration and slow to load.
        var loadParams = new PackageLoadParameters { LoadAssemblyReferences = loadAssemblyReferences, AutoLoadTemporaryAssets = false };
        // OpenDevelop fusion: consumers built straight from this checkout (raw file references,
        // not a NuGet-restored install) never have these two packages under PackageStore's
        // NuGet-cache identity, so the lookup below always misses on non-Windows dev hosts. When
        // STRIDE_SOURCE_ROOT is set (OpenDevelop's addin sets it before touching this code - see
        // doc/technotes/stride-game-studio.md), fall back to the known source-tree .sdpkg path
        // instead of re-implementing package resolution.
        var sourceRoot = Environment.GetEnvironmentVariable("STRIDE_SOURCE_ROOT");
        foreach (var packageInfo in new[]
        {
            new { Name = "Stride.Assets.Presentation", Version = StrideVersion.NuGetVersion, SourceRelativePath = "sources/editor/Stride.Assets.Presentation/Stride.Assets.Presentation.sdpkg" },
            new { Name = "Stride.SpriteStudio.Offline", Version = StrideVersion.NuGetVersion, SourceRelativePath = "sources/engine/Stride.SpriteStudio.Offline/Stride.SpriteStudio.Offline.sdpkg" },
        })
        {
            var logger = new LoggerResult();
            var packageFile = PackageStore.Instance.GetPackageFileName(packageInfo.Name, new PackageVersionRange(new PackageVersion(packageInfo.Version)));
            if (packageFile is null && sourceRoot is not null)
            {
                var candidate = Path.Combine(sourceRoot, packageInfo.SourceRelativePath);
                if (File.Exists(candidate))
                    packageFile = new UFile(candidate);
            }
            if (packageFile is null)
                throw new InvalidOperationException($"Could not find package {packageInfo.Name} {packageInfo.Version}. Ensure packages have been resolved.");
            var package = Package.Load(logger, packageFile.ToOSPath(), loadParams);
            if (logger.HasErrors)
                throw new InvalidOperationException($"Could not load package {packageInfo.Name}:{Environment.NewLine}{logger.ToText()}");

            TemplateManager.RegisterPackage(package);
        }

        DotNetNewTemplateBridge.RegisterProjectTemplates();
    }
}
