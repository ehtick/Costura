﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;

#if NETCORE
using System.Runtime.Loader;
#endif

internal static class ILTemplateWithUnmanagedHandler
{
    private static object nullCacheLock = new object();
    private static Dictionary<string, bool> nullCache = new Dictionary<string, bool>();

    private static string tempBasePath;

    private static Dictionary<string, string> assemblyNames = new Dictionary<string, string>();
    private static Dictionary<string, string> symbolNames = new Dictionary<string, string>();

    private static List<string> preload32List = new List<string>();
    private static List<string> preload64List = new List<string>();

    private static Dictionary<string, string> checksums = new Dictionary<string, string>();

    private static int isAttached;

    public static void Attach()
    {
        if (Interlocked.Exchange(ref isAttached, 1) == 1)
        {
            return;
        }

#if !NETCORE
        var currentDomain = AppDomain.CurrentDomain;

        // Make sure the target framework is set in order not to interfere with AppContext switches initialization
        // See https://github.com/Fody/Costura/issues/633 for full explanation
        var setupInformation = currentDomain.GetType()?.GetProperty("SetupInformation")?.GetValue(currentDomain);
        var targetFrameworkNameProperty = setupInformation?.GetType()?.GetProperty("TargetFrameworkName");
        if (targetFrameworkNameProperty is not null && targetFrameworkNameProperty.GetValue(setupInformation) is null)
        {
            var targetFrameworkAttribute = (TargetFrameworkAttribute)Assembly.GetCallingAssembly()?.GetCustomAttribute(typeof(TargetFrameworkAttribute));
            var targetFrameworkName = targetFrameworkAttribute?.FrameworkName;
            if (targetFrameworkName is not null)
            {
                currentDomain.SetData("TargetFrameworkName", targetFrameworkName);
            }
        }
#endif

        //Create a unique Temp directory for the application path.
        var md5Hash = "To be replaced at compile time";
        var prefixPath = Path.Combine(Path.GetTempPath(), "Costura");
        tempBasePath = Path.Combine(prefixPath, md5Hash);

        // Preload
        var unmanagedAssemblies = IntPtr.Size == 8 ? preload64List : preload32List;
        Common.PreloadUnmanagedLibraries(md5Hash, tempBasePath, unmanagedAssemblies, checksums);

#if NETCORE
        AssemblyLoadContext.Default.Resolving += ResolveAssembly;
#else
        currentDomain.AssemblyResolve += ResolveAssembly;
#endif
    }

#if NETCORE
    public static Assembly ResolveAssembly(AssemblyLoadContext assemblyLoadContext, AssemblyName assemblyName)
#else
    public static Assembly ResolveAssembly(object sender, ResolveEventArgs e)
#endif
    {
#if NETCORE
        var assemblyNameAsString = assemblyName.Name;
#else
        var assemblyNameAsString = e.Name;
        var assemblyName = new AssemblyName(assemblyNameAsString);
#endif
        lock (nullCacheLock)
        {
            if (nullCache.ContainsKey(assemblyNameAsString))
            {
                return null;
            }
        }

        var requestedAssemblyName = new AssemblyName(assemblyNameAsString);

        var assembly = Common.ReadExistingAssembly(requestedAssemblyName);
        if (assembly is not null)
        {
            return assembly;
        }

        Common.Log("Loading assembly '{0}' into the AppDomain", requestedAssemblyName);

        assembly = Common.ReadFromDiskCache(tempBasePath, requestedAssemblyName);
        if (assembly is not null)
        {
            return assembly;
        }

        assembly = Common.ReadFromEmbeddedResources(assemblyNames, symbolNames, requestedAssemblyName);
        if (assembly is null)
        {
            lock (nullCacheLock)
            {
                nullCache[assemblyNameAsString] = true;
            }

            // Handles re-targeted assemblies like PCL
            if ((requestedAssemblyName.Flags & AssemblyNameFlags.Retargetable) != 0)
            {
                assembly = Assembly.Load(requestedAssemblyName);
            }
        }

        return assembly;
    }
}
