//
// MSBuildTextEditorExtension.cs
//
// Authors:
//       Mikayla Hutchinson <m.j.hutchinson@gmail.com>
//
// Copyright (C) 2014 Xamarin Inc. (http://www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoDevelop.Ide.CodeCompletion;
using MonoDevelop.Xml.Completion;
using MonoDevelop.Xml.Dom;
using MonoDevelop.Xml.Editor;
using MonoDevelop.Xml.Parser;
using ProjectFileTools.NuGetSearch.Contracts;
using ProjectFileTools.NuGetSearch.Feeds;
using ProjectFileTools.NuGetSearch.Search;
using ProjectFileTools.NuGetSearch.Feeds.Disk;
using ProjectFileTools.NuGetSearch.Feeds.Web;
using ProjectFileTools.NuGetSearch.IO;
using MonoDevelop.Components.Commands;
using MonoDevelop.Ide.Editor;
using MonoDevelop.Ide;

namespace MonoDevelop.MSBuildEditor
{
	class MSBuildTextEditorExtension : BaseXmlEditorExtension
	{
		public static readonly string MSBuildMimeType = "application/x-msbuild";

		public IPackageSearchManager PackageSearchManager { get; set; }

		protected override void Initialize ()
		{
			base.Initialize ();

			//we don't have a MEF composition here, set it up manually
			PackageSearchManager = new PackageSearchManager (
				new MonoDevelopPackageFeedRegistry (),
				new PackageFeedFactorySelector (new IPackageFeedFactory [] {
					new NuGetDiskFeedFactory (new FileSystem()),
					new NuGetV3ServiceFeedFactory (new WebRequestFactory()),
				})
			);
		}

		MSBuildParsedDocument GetDocument ()
		{
			return (MSBuildParsedDocument)DocumentContext.ParsedDocument;
		}

		public override Task<ICompletionDataList> HandleCodeCompletionAsync (CodeCompletionContext completionContext, CompletionTriggerInfo triggerInfo, CancellationToken token = default (CancellationToken))
		{
			var expressionCompletion = HandleExpressionCompletion (completionContext, triggerInfo, token);
			if (expressionCompletion != null) {
				return Task.FromResult (expressionCompletion);
			}

			return base.HandleCodeCompletionAsync (completionContext, triggerInfo, token);
		}

		protected override Task<CompletionDataList> GetElementCompletions (CancellationToken token)
		{
			var list = new CompletionDataList ();
			AddMiscBeginTags (list);

			var path = GetCurrentPath ();

			if (path.Count == 0) {
				list.Add (new XmlCompletionData ("Project", XmlCompletionData.DataType.XmlElement));
				return Task.FromResult (list);
			}

			var rr = ResolveElement (path);
			if (rr == null)
				return Task.FromResult (list);

			foreach (var c in rr.BuiltinChildren)
				list.Add (new XmlCompletionData (c, XmlCompletionData.DataType.XmlElement));

			foreach (var item in GetInferredChildren (rr)) {
				list.Add (new MSBuildCompletionData (item, GetDocument ()));
			}

			return Task.FromResult (list);
		}

		class MSBuildCompletionData : XmlCompletionData
		{
			readonly MSBuildParsedDocument doc;
			readonly BaseInfo info;
			string description;

			public MSBuildCompletionData (BaseInfo info, MSBuildParsedDocument doc)
				: base (info.Name, info.Description, DataType.XmlElement)
			{
				this.info = info;
				this.doc = doc;
			}

			public override string Description {
				get {
					return description ?? (description = GetDescription () ?? "");
				}
			}

			string GetDescription ()
			{
				return AppendSeenIn (description);
			}

			string AppendSeenIn (string baseDesc)
			{
				if (doc == null) {
					return description;
				}

				IEnumerable<string> seenIn = doc.Context.GetFilesSeenIn (info);
				StringBuilder sb = null;

				foreach (var s in seenIn) {
					if (sb == null) {
						sb = new StringBuilder ();
						if (!string.IsNullOrEmpty (baseDesc)) {
							sb.AppendLine (baseDesc);
						}
						sb.AppendLine ("Seen in: ");
						sb.AppendLine ();
					}
					sb.AppendLine ($"    {s}");
				}
				return sb?.ToString () ?? baseDesc;
			}
		}

		IEnumerable<BaseInfo> GetInferredChildren (ResolveResult rr)
		{
			var doc = GetDocument ();
			if (doc == null)
				return new BaseInfo [0];

			if (rr.ElementType == MSBuildKind.Item) {
				return doc.Context.GetItemMetadata (rr.ElementName, false);
			}

			if (rr.ChildType.HasValue) {
				switch (rr.ChildType.Value) {
				case MSBuildKind.Item:
					return doc.Context.GetItems ();
				case MSBuildKind.Task:
					return doc.Context.GetTasks ();
				case MSBuildKind.Property:
					return doc.Context.GetProperties (false);
				}
			}
			return new BaseInfo [0];
		}

		protected override Task<CompletionDataList> GetAttributeCompletions (IAttributedXObject attributedOb,
			Dictionary<string, string> existingAtts, CancellationToken token)
		{
			var path = GetCurrentPath ();

			var rr = ResolveElement (path);
			if (rr == null)
				return null;

			var list = new CompletionDataList ();
			foreach (var a in rr.BuiltinAttributes)
				if (!existingAtts.ContainsKey (a))
					list.Add (new XmlCompletionData (a, XmlCompletionData.DataType.XmlAttribute));

			var inferredAttributes = GetInferredAttributes (rr);
			if (inferredAttributes != null)
				foreach (var a in inferredAttributes)
					if (!existingAtts.ContainsKey (a))
						list.Add (new XmlCompletionData (a, XmlCompletionData.DataType.XmlAttribute));

			return Task.FromResult (list);
		}

		IEnumerable<string> GetInferredAttributes (ResolveResult rr)
		{
			var doc = GetDocument ();
			if (doc == null) {
				return Array.Empty<string> ();
			}

			//metadata as attributes
			if (rr.ElementType == MSBuildKind.Item && doc.ToolsVersion.IsAtLeast (MSBuildToolsVersion.V15_0)) {
				return doc.Context.GetItemMetadata (rr.ElementName, false).Where (a => !a.WellKnown).Select (a => a.Name);
			}

			if (rr.ElementType != MSBuildKind.Task) {
				var result = new HashSet<string> ();
				foreach (var task in doc.Context.GetTask (rr.ElementName)) {
					foreach (var p in task.Parameters) {
						result.Add (p);
					}
				}
				return result;
			}

			return null;
		}

		protected override Task<CompletionDataList> GetAttributeValueCompletions (IAttributedXObject attributedOb, XAttribute att, CancellationToken token)
		{
			var path = GetCurrentPath ();

			var rr = ResolveElement (path);
			if (rr == null) {
				return null;
			}

			int triggerLength = ((IXmlParserContext)Tracker.Engine).KeywordBuilder.Length;
			int startIdx = Editor.CaretOffset - triggerLength;

			if ((rr.ElementType == MSBuildKind.Import || rr.ElementType == MSBuildKind.Project) && rr.AttributeName == "Sdk") {
				return GetSdkCompletions (token);
			}

			if (rr.ElementType == MSBuildKind.Item && rr.ElementName == "PackageReference") {
				var tfm = GetDocument ().Frameworks.FirstOrDefault ()?.ToString () ?? ".NETStandard,Version=v2.0";
				if (rr.AttributeName == "Include") {
					string name = ((IXmlParserContext)Tracker.Engine).KeywordBuilder.ToString ();
					if (string.IsNullOrWhiteSpace (name)) {
						return null;
					}
					return Task.FromResult<CompletionDataList> (
						new PackageSearchCompletionDataList (
							name,
							(n) => PackageSearchManager.SearchPackageNames (n.ToLower (), tfm)
						) {
							TriggerWordStart = startIdx,
							TriggerWordLength = triggerLength
						}
					);
				}
				if (rr.AttributeName == "Version") {
					var name = path.OfType<XElement> ().Last ().Attributes.FirstOrDefault (a => a.Name.FullName == "Include")?.Value;
					if (string.IsNullOrEmpty (name)) {
						return null;
					}
					return Task.FromResult<CompletionDataList> (
						new PackageSearchCompletionDataList (
							PackageSearchManager.SearchPackageVersions (name, tfm)
						) {
							TriggerWordStart = startIdx,
							TriggerWordLength = triggerLength
						}
					);
				}
			}

			return base.GetAttributeValueCompletions (attributedOb, att, token);
		}

		Task<CompletionDataList> GetSdkCompletions (CancellationToken token)
		{
			var list = new CompletionDataList ();
			var doc = GetDocument ();
			if (doc == null) {
				return Task.FromResult (list);
			}

			var sdks = new HashSet<string> ();

			var resolver = doc.SdkResolver;
			foreach (var sdk in resolver.GetRegisteredSdks ()) {
				if (sdks.Add (sdk.Name)) {
					list.Add (System.IO.Path.GetFileName (sdk.Name));
				}
			}

			//TODO: how can we find SDKs in the non-default locations?
			return Task.Run (() => {
				foreach (var d in System.IO.Directory.GetDirectories (resolver.DefaultSdkPath)) {
					string name = System.IO.Path.GetFileName (d);
					if (sdks.Add (name)) {
						list.Add (name);
					}
				}
				return list;
			}, token);
		}

		static ResolveResult ResolveElement (IList<XObject> path)
		{
			//need to look up element by walking how the path, since at each level, if the parent has special children,
			//then that gives us information to identify the type of its children
			MSBuildElement el = null;
			string elName = null, attName = null;
			for (int i = 0; i < path.Count; i++) {
				var xatt = path [i] as XAttribute;
				if (xatt != null) {
					attName = xatt.Name.Name;
					break;
				}
				//if children of parent is known to be arbitrary data, don't go into it
				if (el != null && el.ChildType == MSBuildKind.Data)
					break;
				//code completion is forgiving, all we care about best guess resolve for deepest child
				var xel = path [i] as XElement;
				if (xel != null && xel.Name.Prefix == null) {
					elName = xel.Name.Name;
					el = MSBuildElement.Get (elName, el);
					if (el != null)
						continue;
				}
				el = null;
				elName = null;
			}
			if (el == null)
				return null;

			return new ResolveResult {
				AttributeName = attName,
				ElementName = elName,
				ElementType = el.Kind,
				ChildType = el.ChildType,
				BuiltinAttributes = el.Attributes,
				BuiltinChildren = el.Children,
			};
		}

		class ResolveResult
		{
			public string AttributeName;
			public string ElementName;
			public MSBuildKind? ElementType;
			public MSBuildKind? ChildType;
			public IEnumerable<string> BuiltinAttributes;
			public IEnumerable<string> BuiltinChildren;
		}

		ICompletionDataList HandleExpressionCompletion (CodeCompletionContext completionContext, CompletionTriggerInfo triggerInfo, CancellationToken token)
		{
			var doc = GetDocument ();
			if (doc == null)
				return null;

			var path = GetCurrentPath ();
			var rr = ResolveElement (path);

			if (rr == null || rr.ElementType == null) {
				return null;
			}

			var state = Tracker.Engine.CurrentState;
			bool isAttribute = state is Xml.Parser.XmlAttributeValueState;
			if (isAttribute) {
				//FIXME: assume all attributes accept expressions for now
			} else if (state is Xml.Parser.XmlRootState) {
				if (rr.ChildType != MSBuildKind.Expression)
					return null;
			} else {
				return null;
			}

			//FIXME: This is very rudimentary. We should parse the expression for real.
			int currentPosition = Editor.CaretOffset;
			int lineStart = Editor.GetLine (Editor.CaretLine).Offset;
			int expressionStart = currentPosition - Tracker.Engine.CurrentStateLength;
			if (isAttribute && GetAttributeValueDelimiter (Tracker.Engine) != 0) {
				expressionStart += 1;
			}
			int start = Math.Max (expressionStart, lineStart);
			var expression = Editor.GetTextAt (start, currentPosition - start);

			if (expression.Length < 2) {
				return null;
			}

			//trigger on letter after $(, @(
			if (expression.Length >= 3 && char.IsLetter (expression [expression.Length - 1]) && expression [expression.Length - 2] == '(') {
				char c = expression [expression.Length - 3];
				if (c == '$') {
					return new CompletionDataList (GetPropertyExpressionCompletions (doc)) { TriggerWordLength = 1 };
				}
				if (c == '@') {
					return new CompletionDataList (GetItemExpressionCompletions (doc)) { TriggerWordLength = 1 };
				}
				return null;
			}

			//trigger on $(, @(
			if (expression [expression.Length - 1] == '(') {
				char c = expression [expression.Length - 2];
				if (c == '$') {
					return new CompletionDataList (GetPropertyExpressionCompletions (doc));
				}
				if (c == '@') {
					return new CompletionDataList (GetItemExpressionCompletions (doc));
				}
				return null;
			}

			return null;
		}

		//FIXME: this is fragile, need API in core
		static char GetAttributeValueDelimiter (XmlParser parser)
		{
			var ctx = (IXmlParserContext)parser;
			switch (ctx.StateTag) {
			case 3: return '"';
			case 2: return '\'';
			default: return (char)0;
			}
		}

		IEnumerable<CompletionData> GetItemExpressionCompletions (MSBuildParsedDocument doc)
		{
			foreach (var item in doc.Context.GetItems ()) {
				yield return new CompletionData (item.Name, MonoDevelop.Ide.Gui.Stock.Class, item.Description);
			}
		}

		IEnumerable<CompletionData> GetPropertyExpressionCompletions (MSBuildParsedDocument doc)
		{
			foreach (var prop in doc.Context.GetProperties (true)) {
				yield return new CompletionData (prop.Name, MonoDevelop.Ide.Gui.Stock.Class, prop.Description);
			}
		}

		Import GetImportAtLocation (DocumentLocation location)
		{
			var doc = GetDocument ();
			if (doc == null) {
				return null;
			}

			var xobj = Find (doc.XDocument, location);
			if (xobj == null) {
				return null;
			}

			return doc.Context.Annotations.Get<Import> (xobj);
		}

		[CommandHandler (Refactoring.RefactoryCommands.GotoDeclaration)]
		void GotoDefinition()
		{
			var import = GetImportAtLocation (Editor.CaretLocation);
			if (import != null) {
				//FIXME: can we open the doc with the same context i.e. as a child of this?
				// That would improve drilldown and find refs accuracy but would run into issues
				// when drilling down into the same child from multiple parents.
				// We'd probably need something like the shared projects context dropdown.
				IdeApp.Workbench.OpenDocument (import.Filename, this.DocumentContext.Project, true);
			}
		}

		[CommandUpdateHandler (Refactoring.RefactoryCommands.GotoDeclaration)]
		void UpdateGotoDefinition (CommandInfo info)
		{
			info.Enabled = GetImportAtLocation (Editor.CaretLocation) != null;
		}

		//FIXME: binary search
		XObject Find (XContainer container, DocumentLocation location)
		{
			var node = container.AllDescendentNodes.FirstOrDefault (n => n.Region.Contains (location));
			if (node != null) {
				if (node is IAttributedXObject attContainer) {
					var att = attContainer.Attributes.FirstOrDefault (n => n.Region.Contains (location));
					if (att != null) {
						return att;
					}
				}
			}
			return node;
		}
	}
}