﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.Build.Framework;
using MonoDevelop.Core;
using MonoDevelop.Core.Assemblies;
using MonoDevelop.MSBuild;
using MonoDevelop.MSBuild.SdkResolution;

namespace MonoDevelop.MSBuildEditor
{
	[Export (typeof (IRuntimeInformation))]
	class MonoDevelopRuntimeInformation : IRuntimeInformation
	{
		readonly MonoDevelopSdkResolver sdkResolver;
		Dictionary<SdkReference, string> resolvedSdks = new Dictionary<SdkReference, string> ();

		public MonoDevelopRuntimeInformation ()
		{
			var runtime = Runtime.SystemAssemblyService.DefaultRuntime;
			ToolsVersion = "15.0";
			BinPath = runtime.GetMSBuildBinPath (ToolsVersion);
			ToolsPath = runtime.GetMSBuildToolsPath (ToolsVersion);
			sdkResolver = new MonoDevelopSdkResolver (runtime);

			var extensionPaths = GetExtensionsPaths (runtime).ToList ();
			SearchPaths = new Dictionary<string, IReadOnlyList<string>> {
				{ "MSBuildExtensionsPath", extensionPaths },
				{ "MSBuildExtensionsPath32", extensionPaths },
				{ "MSBuildExtensionsPath64", extensionPaths }
			};
		}

		IEnumerable<string> GetExtensionsPaths (TargetRuntime runtime)
		{
			yield return runtime.GetMSBuildExtensionsPath ();
			if (Platform.IsMac) {
				yield return "/Library/Frameworks/Mono.framework/External/xbuild";
			}
		}

		public string ToolsVersion { get; }

		public string BinPath { get; }

		public string ToolsPath { get; }

		public IReadOnlyDictionary<string, IReadOnlyList<string>> SearchPaths { get; }

		public string SdksPath => sdkResolver.DefaultSdkPath;

		public IList<SdkInfo> GetRegisteredSdks () => sdkResolver.GetRegisteredSdks ();

		public string GetSdkPath (SdkReference sdk, string projectFile, string solutionPath)
		{
			if (!resolvedSdks.TryGetValue (sdk, out string path)) {
				try {
					path = sdkResolver.GetSdkPath (sdk, projectFile, solutionPath);
				} catch (Exception ex) {
					LoggingService.LogError ("Error in SDK resolver", ex);
				}
				resolvedSdks[sdk] = path;
			}
			return path;
		}
	}
}
