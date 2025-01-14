// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using MonoDevelop.MSBuild.Evaluation;
using MonoDevelop.MSBuild.Language;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo ("MonoDevelop.MSBuild.Tests, PublicKey=0024000004800000940000000602000000240000525341310004000001000100510a1c03c181816c65be87b8fd908657f57154bfe3304485a0613251255e13b1313f6acbd296bc807779dff01271101cc7c341357a5af16be39072d9ff5b3fbf72c3100aab5b55775b4b5494eb5c93209755fe3b4eb95f64c790bb1bab867217919b365f120d8885769da792a412d903b0a953357b611d71097fdfce4caf62fa")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo ("MonoDevelop.MSBuild.Editor, PublicKey=0024000004800000940000000602000000240000525341310004000001000100510a1c03c181816c65be87b8fd908657f57154bfe3304485a0613251255e13b1313f6acbd296bc807779dff01271101cc7c341357a5af16be39072d9ff5b3fbf72c3100aab5b55775b4b5494eb5c93209755fe3b4eb95f64c790bb1bab867217919b365f120d8885769da792a412d903b0a953357b611d71097fdfce4caf62fa")]

namespace MonoDevelop.MSBuild
{
	static class LoggingService
	{
		public static void LogDebug (string message) => Console.WriteLine (message);
		public static void LogError (string message, Exception ex) => LogError ($"{message}: {ex}");
		public static void LogError (string message) => Console.Error.WriteLine (message);
		internal static void LogWarning (string message) => Console.WriteLine (message);
		internal static void LogInfo (string message) => Console.WriteLine (message);
	}

	static class MSBuildHost
	{
		public static class Options
		{
			public static bool ShowPrivateSymbols => false;
		}
	}
}