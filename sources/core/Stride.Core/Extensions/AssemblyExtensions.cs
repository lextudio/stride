// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using System.Reflection;

namespace Stride.Core.Extensions;

public static class AssemblyExtensions
{
    /// <summary>
    /// Enumerates an assembly's types, tolerating types that fail to load.
    /// </summary>
    /// <remarks>
    /// <para>
    ///   <see cref="Assembly.GetTypes"/> is all-or-nothing: a single type whose dependencies cannot be
    ///   resolved makes the whole enumeration throw, yielding nothing. That is rarely what a scan for
    ///   types matching some criterion wants — the unloadable types are usually not the ones being
    ///   looked for.
    /// </para>
    /// <para>
    ///   This matters most when an assembly is loaded into a host that already provides a conflicting
    ///   version of one of its dependencies: only the types touching that dependency fail, and
    ///   everything else remains perfectly usable.
    /// </para>
    /// </remarks>
    /// <param name="assembly">The assembly to enumerate.</param>
    /// <returns>The types that loaded successfully.</returns>
    [DebuggerNonUserCode]
    public static IEnumerable<Type> GetFullyLoadedTypes(this Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.NotNull();
        }
    }
}
