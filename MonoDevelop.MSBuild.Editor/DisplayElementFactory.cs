// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Adornments;
using MonoDevelop.MSBuild.Editor.Host;
using MonoDevelop.MSBuild.Language;
using MonoDevelop.MSBuild.PackageSearch;
using MonoDevelop.MSBuild.Schema;

using ProjectFileTools.NuGetSearch.Contracts;
using ProjectFileTools.NuGetSearch.Feeds;

namespace MonoDevelop.MSBuild.Editor
{
	[Export, PartCreationPolicy (CreationPolicy.Shared)]
	class DisplayElementFactory
	{
		[Import]
		IMSBuildEditorHost Host { get; set; }

		public async Task<object> GetInfoTooltipElement (MSBuildRootDocument doc, BaseInfo info, MSBuildResolveResult rr, CancellationToken token)
		{
			object nameElement = GetNameElement (info);
			if (nameElement == null) {
				return null;
			}

			var imageElement = GetImageElement (info);
			if (imageElement != null) {
				nameElement = new ContainerElement (
					ContainerElementStyle.Wrapped | ContainerElementStyle.VerticalPadding,
					imageElement, nameElement
				);
			}

			var elements = new List<object> { nameElement };

			switch (info.Description.DisplayElement) {
			case ISymbol symbol:
				await AddSymbolDescriptionElements (symbol, elements.Add, token);
				break;
			case object obj:
				elements.Add (obj);
				break;
			default:
				var descStr = DescriptionFormatter.GetDescription (info, doc, rr);
				if (!string.IsNullOrEmpty (descStr)) {
					elements.Add (new ClassifiedTextElement (FormatDescriptionText (descStr)));
				}
				break;
			}

			var seenIn = GetSeenInElement (info, doc);
			if (seenIn != null) {
				elements.Add (seenIn);
			}

			var deprecationMessage = GetDeprecationMessage (info);
			if (deprecationMessage != null) {
				elements.Add (deprecationMessage);
			}

			return elements.Count == 1
				? elements[0]
				: new ContainerElement (ContainerElementStyle.Stacked | ContainerElementStyle.VerticalPadding, elements);
		}

		static ClassifiedTextElement GetDeprecationMessage (BaseInfo info)
		{
			if (info is ValueInfo val && val.IsDeprecated) {
				var msg = string.IsNullOrEmpty (val.DeprecationMessage) ? "Deprecated" : $"Deprecated: {val.DeprecationMessage}";
				return new ClassifiedTextElement (new ClassifiedTextRun ("syntax error", msg));
			}
			return null;
		}

		public ClassifiedTextElement GetNameElement (BaseInfo info)
		{
			var label = DescriptionFormatter.GetTitle (info);
			if (label.kind == null) {
				return null;
			}

			var runs = new List<ClassifiedTextRun> ();

			runs.Add (new ClassifiedTextRun (PredefinedClassificationTypeNames.Keyword, label.kind));
			runs.Add (new ClassifiedTextRun (PredefinedClassificationTypeNames.WhiteSpace, " "));
			runs.Add (new ClassifiedTextRun (PredefinedClassificationTypeNames.Identifier, label.name));

			string typeInfo = null;
			if (info is ValueInfo vi) {
				var tdesc = vi.GetTypeDescription ();
				if (tdesc.Count > 0) {
					typeInfo = string.Join (" ", tdesc);
				}
			}

			if (info is FunctionInfo fi) {
				typeInfo = fi.ReturnTypeString;
				if (!fi.IsProperty) {
					runs.Add (new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, "("));

					bool first = true;
					foreach (var p in fi.Parameters) {
						if (first) {
							first = false;
						} else {
							runs.Add (new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, ", "));
						}

						runs.Add (new ClassifiedTextRun (PredefinedClassificationTypeNames.SymbolReference, p.Name));
						runs.Add (new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, " : "));
						runs.Add (new ClassifiedTextRun (PredefinedClassificationTypeNames.Type, p.Type));
					}
					runs.Add (new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, ")"));
				}
			}

			if (typeInfo != null) {
				runs.Add (new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, " : "));
				runs.Add (new ClassifiedTextRun (PredefinedClassificationTypeNames.Type, typeInfo));
			}

			return new ClassifiedTextElement (runs);
		}

		public ContainerElement GetSeenInElement (BaseInfo info, MSBuildRootDocument doc)
		{
			var seenIn = doc.GetFilesSeenIn (info).ToList ();
			if (seenIn.Count == 0) {
				return null;
			}

			Func<string, (string prefix, string remaining)?> shorten = null;

			var elements = new List<ClassifiedTextElement> ();

			int count = 0;
			foreach (var s in seenIn) {
				if (count == 5) {
					elements.Add (new ClassifiedTextElement (new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, "[more in Find References]")));
					break;
				}
				count++;

				//factor out some common prefixes into variables
				//we do this instead of using the original string, as the result is simpler
				//and easier to understand
				shorten = shorten ?? CreateFilenameShortener (doc.RuntimeInformation);
				var replacement = shorten (s);
				if (!replacement.HasValue) {
					elements.Add (new ClassifiedTextElement (new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, s)));
					continue;
				}

				elements.Add (new ClassifiedTextElement (
					new ClassifiedTextRun (PredefinedClassificationTypeNames.SymbolReference, replacement.Value.prefix),
					new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, replacement.Value.remaining, () => OpenFile (s), s)
				));
			}

			if (elements.Count == 0) {
				return null;
			}

			elements.Insert (0, new ClassifiedTextElement (new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, "Seen in:")));
			return new ContainerElement (ContainerElementStyle.Stacked, elements);
		}

		void OpenFile (string path)
		{
			if (System.IO.File.Exists (path)) {
				Host.OpenFile (path, 0);
			} else if (System.IO.Directory.Exists (path)) {
				Process.Start (path);
			}
		}

		public object GetResolvedPathElement (List<NavigationAnnotation> navs)
		{
			if (navs.Count == 1) {
				return new ClassifiedTextElement (
					new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, "Resolved Path:"),
					new ClassifiedTextRun (PredefinedClassificationTypeNames.WhiteSpace, " "),
					new ClassifiedTextRun (PredefinedClassificationTypeNames.Literal, navs[0].Path, () => OpenFile (navs[0].Path))
				);
			}

			var elements = new List<ClassifiedTextElement> ();
			elements.Add (new ClassifiedTextElement (new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, "Resolved Paths:")));

			int i = 0;
			foreach (var location in navs) {
				elements.Add (new ClassifiedTextElement (new ClassifiedTextRun (PredefinedClassificationTypeNames.Literal, location.Path, () => OpenFile (location.Path))));
				if (i == 5) {
					elements.Add (new ClassifiedTextElement (new ClassifiedTextRun (PredefinedClassificationTypeNames.Other, "[more in Go to Definition]")));
					break;
				}
			}

			return new ContainerElement (ContainerElementStyle.Stacked, elements);
		}

		/// <summary>
		/// Shortens filenames by extracting common prefixes into MSBuild properties. Returns null if the name could not be shortened in this way.
		/// </summary>
		Func<string, (string prefix, string remaining)?> CreateFilenameShortener (IRuntimeInformation runtimeInfo)
		{
			var prefixes = GetPrefixes (runtimeInfo);
			return s => GetLongestReplacement (s, prefixes);
		}

		static List<(string prefix, string subst)> GetPrefixes (IRuntimeInformation runtimeInfo)
		{
			var list = new List<(string prefix, string subst)> {
				(runtimeInfo.BinPath, "$(MSBuildBinPath)"),
				(runtimeInfo.ToolsPath, "$(MSBuildToolsPath)")
			};
			foreach (var extPath in runtimeInfo.SearchPaths["MSBuildExtensionsPath"]) {
				list.Add ((extPath, "$(MSBuildExtensionsPath)"));
			}
			var sdksPath = runtimeInfo.SdksPath;
			if (sdksPath != null) {
				list.Add ((sdksPath, "$(MSBuildSDKsPath)"));
			}
			return list;
		}

		static (string prefix, string remaining)? GetLongestReplacement (string val, List<(string prefix, string subst)> replacements)
		{
			(string prefix, string subst)? longestReplacement = null;
			foreach (var replacement in replacements) {
				if (val.StartsWith (replacement.prefix, StringComparison.OrdinalIgnoreCase)) {
					if (!longestReplacement.HasValue || longestReplacement.Value.prefix.Length < replacement.prefix.Length) {
						longestReplacement = replacement;
					}
				}
			}

			if (longestReplacement.HasValue) {
				return (longestReplacement.Value.subst, val.Substring (longestReplacement.Value.prefix.Length));
			}

			return null;
		}

		public ImageElement GetImageElement (BaseInfo info)
		{
			var id = GetKnownImageIdForInfo (info, false);
			return id.HasValue ? new ImageElement (id.Value.ToImageId ()) : null;
		}

		public ImageElement GetImageElement (KnownImages image) => new ImageElement (image.ToImageId (), image.ToString ());

		public ImageElement GetImageElement (FeedKind kind) => GetImageElement (GetPackageImageId (kind));

		KnownImages GetPackageImageId (FeedKind kind)
		{
			switch (kind) {
			case FeedKind.Local: return KnownImages.FolderClosed;
			case FeedKind.NuGet: return KnownImages.NuGet;
			default: return KnownImages.GenericNuGetPackage;
			}
		}

		static KnownImages? GetKnownImageIdForInfo (BaseInfo info, bool isPrivate)
		{
			switch (info) {
			case MSBuildElementSyntax el:
				if (!el.IsAbstract)
					return KnownImages.IntellisenseKeyword;
				break;
			case MSBuildAttributeSyntax att:
				if (!att.IsAbstract) {
					return KnownImages.IntellisenseKeyword;
				}
				break;
			case ItemInfo _:
				return isPrivate ? KnownImages.MSBuildItemPrivate : KnownImages.MSBuildItem;
			case PropertyInfo _:
				return isPrivate ? KnownImages.MSBuildPropertyPrivate : KnownImages.MSBuildProperty;
			case TargetInfo _:
				return isPrivate ? KnownImages.MSBuildTargetPrivate : KnownImages.MSBuildTarget;
			case MetadataInfo _:
				return isPrivate ? KnownImages.MSBuildMetadata : KnownImages.MSBuildMetadataPrivate;
			case TaskInfo _:
				return KnownImages.MSBuildTask;
			case ConstantInfo _:
				return KnownImages.MSBuildConstant;
			case FileOrFolderInfo value:
				return value.IsFolder ? KnownImages.FolderClosed : KnownImages.GenericFile;
			case FrameworkInfo _:
				return KnownImages.MSBuildFrameworkId;
			case TaskParameterInfo _:
				return KnownImages.MSBuildTaskParameter;
			case FunctionInfo fi:
				if (fi.IsProperty) {
					//FIXME: can we resolve the msbuild / .net property terminology overloading?
					return KnownImages.Property;
				}
				return KnownImages.Method;
			case ClassInfo _:
				return KnownImages.Class;
			}
			return null;
		}

		static Task AddSymbolDescriptionElements (ISymbol symbol, Action<ClassifiedTextElement> add, CancellationToken token)
		{
			return Task.Run (() => {
				try {
					// MSBuild uses property getters directly but they don't typically have docs.
					// Use the docs from the property instead.
					// FIXME: this doesn't seem to work for the indexer string[]get_Chars, at least on Mono
					if (symbol is IMethodSymbol method && method.MethodKind == MethodKind.PropertyGet) {
						symbol = method.AssociatedSymbol ?? symbol;
					}
					var docs = symbol.GetDocumentationCommentXml (expandIncludes: true, cancellationToken: token);
					if (!string.IsNullOrEmpty (docs)) {
						GetDocsXmlSummaryElement (docs, add);
					}
				} catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested)) {
					LoggingService.LogError ("Error loading docs summary", ex);
				}
			}, token);
		}

		// roslyn's IDocumentationCommentFormattingService seems to be basically unusable
		// without internals access, so do some basic formatting ourselves
		static void GetDocsXmlSummaryElement (string docs, Action<ClassifiedTextElement> addTextElement)
		{
			var docsXml = XDocument.Parse (docs);
			var summaryEl = docsXml.Root?.Element ("summary");
			if (summaryEl == null) {
				return;
			}

			var runs = new List<ClassifiedTextRun> ();

			foreach (var node in summaryEl.Nodes ()) {
				switch (node) {
				case XText text:
					runs.AddRange (FormatDescriptionText (text.Value));
					break;
				case XElement el:
					switch (el.Name.LocalName) {
					case "see":
						ConvertSeeCrefElement (el, runs.Add);
						continue;
					case "attribution":
						continue;
					case "para":
						FlushRuns ();
						var para = RenderXmlDocsPara (el);
						if (para != null) {
							addTextElement (para);
						}
						continue;
					default:
						LoggingService.LogDebug ($"Docs summary has unexpected '{el.Name}' element");
						continue;
					}
				default:
					LoggingService.LogDebug ($"Docs summary has unexpected '{node.NodeType}' node");
					continue;
				}
			}

			void FlushRuns ()
			{
				if (runs.Count > 0) {
					if (!runs.All (r => r is ClassifiedTextRun cr && string.IsNullOrWhiteSpace (cr.Text))) {
						addTextElement (new ClassifiedTextElement (runs));
						runs.Clear ();
					}
				}
			}
		}

		static void ConvertSeeCrefElement (XElement el, Action<ClassifiedTextRun> add)
		{
			var cref = (string)el.Attribute ("cref");
			if (cref != null) {
				var colonIdx = cref.IndexOf (':');
				if (colonIdx > -1) {
					cref = cref.Substring (colonIdx + 1);
				}
				if (!string.IsNullOrEmpty (cref)) {
					add (new ClassifiedTextRun (PredefinedClassificationTypeNames.Type, cref));
				}
			} else {
				LoggingService.LogDebug ("Docs 'see' element is missing cref attribute");
			}
		}

		static ClassifiedTextElement RenderXmlDocsPara (XElement para)
		{
			var runs = new List<ClassifiedTextRun> ();
			foreach (var node in para.Nodes ()) {
				switch (node) {
				case XText text:
					runs.AddRange (FormatDescriptionText (text.Value));
					continue;
				case XElement el:
					switch (el.Name.LocalName) {
					case "see":
						ConvertSeeCrefElement (el, runs.Add);
						continue;
					default:
						LoggingService.LogDebug ($"Docs summary para has unexpected '{el.Name}' element");
						continue;
					}
				default:
					LoggingService.LogDebug ($"Docs summary para has unexpected '{node.NodeType}' node");
					continue;
				}
			}
			return runs.Count > 0 ? new ClassifiedTextElement (runs) : null;
		}

		public object GetPackageInfoTooltip (string packageId, IPackageInfo package, FeedKind feedKind)
		{
			var stackedElements = new List<object> ();

			stackedElements.Add (
				new ContainerElement (
					ContainerElementStyle.Wrapped,
					GetImageElement (feedKind),
					new ClassifiedTextElement (
						new ClassifiedTextRun (PredefinedClassificationTypeNames.Keyword, "package"),
						new ClassifiedTextRun (PredefinedClassificationTypeNames.WhiteSpace, " "),
						new ClassifiedTextRun (PredefinedClassificationTypeNames.Type, package?.Id ?? packageId)
					)
				)
			);

			ClassifiedTextElement descEl;
			if (package != null) {
				var description = !string.IsNullOrWhiteSpace (package.Description) ? package.Description : package.Summary;
				if (string.IsNullOrWhiteSpace (description)) {
					description = package.Summary;
				}
				if (!string.IsNullOrWhiteSpace (description)) {
					descEl = new ClassifiedTextElement (
						new ClassifiedTextRun (PredefinedClassificationTypeNames.NaturalLanguage, description)
					);
				} else {
					descEl = new ClassifiedTextElement (
						new ClassifiedTextRun (PredefinedClassificationTypeNames.Comment, "[no description]")
					);
				}
			} else {
				descEl = new ClassifiedTextElement (
					new ClassifiedTextRun (PredefinedClassificationTypeNames.Comment, "Could not load package information")
				);
			}

			stackedElements.Add (descEl);

			if (package != null) {
				var nugetOrgUrl = package.GetNuGetOrgUrl ();
				if (nugetOrgUrl != null) {
					AddUrlElement (nugetOrgUrl, "Go to package on NuGet.org");
				}

				var projectUrl = package.ProjectUrl != null && Uri.TryCreate (package.ProjectUrl, UriKind.Absolute, out var parsedUrl) && parsedUrl.Scheme == Uri.UriSchemeHttps
					? package.ProjectUrl : null;
				if (projectUrl != null) {
					AddUrlElement (projectUrl, "Go to project URL");
				}

				void AddUrlElement (string url, string linkText) => stackedElements.Add (
					new ClassifiedTextElement (
						new ClassifiedTextRun ("navigableSymbol", linkText, () => Process.Start (url), url)
					)
				);
			}

			return new ContainerElement (
				ContainerElementStyle.Stacked | ContainerElementStyle.VerticalPadding,
				stackedElements);
		}
		// converts text with `` markup into classified runs
		internal static IEnumerable<ClassifiedTextRun> FormatDescriptionText (string description)
		{
			int startIndex = 0;

			while (startIndex < description.Length - 2) {
				int tickIndex = description.IndexOf ('`', startIndex);
				if (tickIndex < 0 || description.Length < tickIndex + 2) {
					break;
				}
				int endTickIndex = description.IndexOf ('`', tickIndex + 1);
				if (endTickIndex < 0) {
					break;
				}
				yield return new ClassifiedTextRun (PredefinedClassificationTypeNames.NaturalLanguage, description.Substring (startIndex, tickIndex - startIndex));

				string codeSegment = description.Substring (tickIndex + 1, endTickIndex - tickIndex - 1);
				yield return new ClassifiedTextRun (PredefinedClassificationTypeNames.SymbolReference, codeSegment);

				startIndex = endTickIndex + 1;
			}

			var length = description.Length - startIndex;
			if (length > 0) {
				yield return new ClassifiedTextRun (PredefinedClassificationTypeNames.NaturalLanguage, description.Substring (startIndex, length));
			}
		}
	}


	static class ImageExtensions
	{
		public static ImageId ToImageId (this KnownImages id) => new ImageId (KnownImagesGuid, (int)id);
		static readonly Guid KnownImagesGuid = KnownImageIds.ImageCatalogGuid;
	}

	enum KnownImages
	{
		// mirror values from Microsoft.VisualStudio.Imaging.KnownImageIds into a limited set
		//so we know which ones we need to ensure exist in VSMac
		Property = KnownImageIds.Property,
		PropertyPrivate = KnownImageIds.PropertyPrivate,
		Method = KnownImageIds.Method,
		MethodPrivate = KnownImageIds.MethodPrivate,
		Reference = KnownImageIds.Reference,
		Add = KnownImageIds.Add,
		NuGet = KnownImageIds.NuGet,
		PackageReference = KnownImageIds.PackageReference,
		FolderClosed = KnownImageIds.FolderClosed,
		BinaryFile = KnownImageIds.BinaryFile,
		Class = KnownImageIds.Class,
		ClassPrivate = KnownImageIds.ClassPrivate,
		Field = KnownImageIds.Field,
		FieldPrivate = KnownImageIds.FieldPrivate,
		Enumeration = KnownImageIds.Enumeration,
		EnumerationPrivate = KnownImageIds.EnumerationPrivate,
		Constant = KnownImageIds.Constant,
		ConstantPrivate = KnownImageIds.ConstantPrivate,
		XMLAttribute = KnownImageIds.XMLAttribute,
		XMLCDataTag = KnownImageIds.XMLCDataTag,
		XMLCommentTag = KnownImageIds.XMLCommentTag,
		XMLElement = KnownImageIds.XMLElement,
		IntellisenseKeyword = KnownImageIds.IntellisenseKeyword,
		Assembly = KnownImageIds.Assembly,
		Action = KnownImageIds.Action,
		DotNETFrameworkDependency = KnownImageIds.DotNETFrameworkDependency,
		Parameter = KnownImageIds.Parameter,
		StatusInformation = KnownImageIds.StatusInformation,

		// this defines the mapping from the MSBuild usage to the icons we're re-using
		// FIXME: improve these icons
		MSBuildProperty = Property,
		MSBuildPropertyPrivate = PropertyPrivate,
		MSBuildItem = Class,
		MSBuildItemPrivate = ClassPrivate,
		MSBuildMetadata = Field,
		MSBuildMetadataPrivate = FieldPrivate,
		MSBuildConstant = Constant,
		MSBuildTarget = Method,
		MSBuildTargetPrivate = MethodPrivate,
		MSBuildTask = Action,
		MSBuildTaskParameter = Parameter,
		MSBuildFrameworkId = DotNETFrameworkDependency,
		GenericFile = BinaryFile,
		Sdk = FolderClosed,
		GenericNuGetPackage = NuGet
	}
}
