//
// MSBuildResolveContext.cs
//
// Author:
//       mhutch <m.j.hutchinson@gmail.com>
//
// Copyright (c) 2014 Xamarin Inc.
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
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using MonoDevelop.Core;
using MonoDevelop.Ide;
using MonoDevelop.Ide.TypeSystem;
using MonoDevelop.Projects.Formats.MSBuild;
using MonoDevelop.Xml.Dom;
using MonoDevelop.Xml.Editor;
using MonoDevelop.MSBuildEditor.ExpressionParser;

namespace MonoDevelop.MSBuildEditor
{
	abstract class MSBuildVisitor
	{
		public void Run (XDocument doc)
		{
			foreach (var el in doc.RootElement.Elements) {
				Traverse (el, null);
			}
		}

		void Traverse (XElement el, MSBuildElement parent)
		{
			var resolved = MSBuildElement.Get (el.Name.FullName, parent);
			if (resolved == null) {
				VisitUnknown (el);
				return;
			}

			VisitResolved (el, resolved);

			switch (resolved.Kind) {
			case MSBuildKind.Task:
				VisitTask (el);
				break;
			case MSBuildKind.Import:
				VisitImport (el);
				break;
			case MSBuildKind.Item:
				VisitItem (el);
				break;
			}

			foreach (var child in el.Elements) {
				Traverse (child, resolved);
			}
		}

		protected virtual void VisitResolved (XElement element, MSBuildElement kind)
		{
		}

		protected virtual void VisitUnknown (XElement element)
		{
		}
		
		protected virtual void VisitTask (XElement element)
		{
		}

		protected virtual void VisitItem (XElement element)
		{
		}

		protected virtual void VisitProperty (XElement element)
		{
		}

		protected virtual void VisitItemReference (XObject parent)
		{
		}

		protected virtual void VisitPropertyReference (XObject parent)
		{
		}

		protected virtual void VisitTaskReference (XObject parent)
		{
		}

		protected virtual void VisitImport (XElement element)
		{
		}
	}

	class MSBuildReferenceCollector : MSBuildVisitor
	{
		readonly Dictionary<string,ItemInfo> items = new Dictionary<string,ItemInfo> (StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string,TaskInfo> tasks = new Dictionary<string,TaskInfo> (StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string,PropertyInfo> properties = new Dictionary<string,PropertyInfo> (StringComparer.OrdinalIgnoreCase);
		readonly Dictionary<string,ParsedImport> imports = new Dictionary<string,ParsedImport> (StringComparer.OrdinalIgnoreCase);
	}

	class DocumentMSBuildVisitor : MSBuildReferenceCollector
	{
		AnnotationTable<XObject, object> annotations = new AnnotationTable<XObject, object> ();
		MSBuildParsedDocument doc;

		protected override void VisitUnknown (XElement element)
		{
			doc.Add (new Error (ErrorType.Error, $"Unknown element '{element.Name.FullName}'", element.Region));
		}

		protected override void VisitResolved (XElement element, MSBuildElement kind)
		{
			ValidateAttributes (element, kind);
			annotations.Add (element, kind);
		}

		protected override void VisitTask (XElement element)
		{
		}

		protected override void VisitImport (XElement element)
		{
		}

		void ValidateAttributes (XElement el, MSBuildElement kind)
		{
			//TODO: check required attributes
			//TODO: validate attribute expressions
			foreach (var att in el.Attributes) {
				var valid = resolved.Attributes.Any (a => att.Name.FullName == a);
				if (!valid) {
					doc.Add (new Error (ErrorType.Error, $"Unknown attribute '{att.Name.FullName}'", att.Region));
				}
			}
		}
	}

	class MSBuildResolveContext
	{
		public XDocument XDocument { get; }
		public string FileName { get; }

		public MSBuildResolveContext (string fileName, XDocument doc)
		{
			XDocument = doc;
		}

		public void Load (MSBuildResolveContext previous)
		{
			
		}

		void Resolve (MSBuildResolveContext previous, XElement el, MSBuildElement parent)
		{
			var resolved = MSBuildElement.Get (el.Name.FullName, parent);
			if (resolved == null) {
				Add (new Error (ErrorType.Error, $"Unknown element '{el.Name.FullName}'", el.Region));
				return;
			}

			annotations.Add (el, resolved);

			foreach (var child in el.Elements) {
				Resolve (previous, child, resolved);
			}

			switch (resolved.Kind) {
			case MSBuildKind.Task:
				//TODO: task validation
				return;
			case MSBuildKind.Import:
				ResolveImport (previous, el, Path.GetDirectoryName (FileName));
				break;
			}

			ValidateAttributes (el, resolved);
		}

		void ResolveImport (MSBuildParsedDocument previous, XElement el, string basePath = null)
		{
			var importAtt = el.Attributes [new XName ("Project")];
			string import = importAtt?.Value;
			if (string.IsNullOrWhiteSpace (import)) {
				Add (new Error (ErrorType.Warning, "Empty value", importAtt.Region));
				return;
			}

			//TODO: use property values when resolving imports
			var importEvalCtx = MSBuildResolveContext.CreateImportEvalCtx (ToolsVersion);

			string filename = importEvalCtx.Evaluate (import);

			if (basePath != null) {
				filename = Path.Combine (basePath, filename);
			}

			if (!Platform.IsWindows) {
				filename = filename.Replace ('\\', '/');
			}

			var fi = new FileInfo (filename);

			if (!fi.Exists) {
				Add (new Error (ErrorType.Warning, "Could not resolve import", importAtt.Region));
				return;
			}

			if (imports.ContainsKey (filename)) {
				return;
			}

			//TODO: reparse this when any imports' mtimes change
			ParsedImport parsedImport;
			if (previous != null && previous.imports.TryGetValue (filename, out parsedImport) && parsedImport.TimeStampUtc <= fi.LastWriteTimeUtc) {
				imports [filename] = parsedImport;
				return;
			}

			var xmlParser = new XmlParser (new XmlRootState (), true);
			try {
				bool useBom;
				System.Text.Encoding encoding;
				string text = Core.Text.TextFileUtility.ReadAllText (filename, out useBom, out encoding);
				xmlParser.Parse (new StringReader (text));
			}
			catch (Exception ex) {
				LoggingService.LogError ("Unhandled error parsing xml document", ex);
			}

			imports [filename] = new ParsedImport (filename, xmlParser.RootState.CreateDocument (), fi.LastWriteTimeUtc);
		}
	}

	/*
		static readonly XName xnProject = new XName ("Project");

		
		readonly HashSet<string> imports = new HashSet<string> ();
		public readonly DateTime TimeStampUtc = DateTime.UtcNow;
		string ToolsVersion;

		Dictionary<string,MSBuildResolveContext> resolvedImports;

		MSBuildEvaluationContext importEvalCtx;

		public IEnumerable<ItemInfo> GetItems ()
		{
			if (resolvedImports == null)
				return items.Values;

			var result = new HashSet<ItemInfo> (items.Values);

			foreach (var import in resolvedImports)
				foreach (var val in import.Value.GetItems ())
					if (NotPrivate (val.Name))
						result.Add (val);

			return result;
		}

		public IEnumerable<MetadataInfo> GetItemMetadata (string itemName)
		{
			ItemInfo item;
			if (items.TryGetValue (itemName, out item))
				return item.Metadata.Values;
			return new MetadataInfo [0];
		}

		public IEnumerable<TaskInfo> GetTasks ()
		{
			if (resolvedImports == null)
				return tasks.Values;

			var result = new HashSet<TaskInfo> (tasks.Values);

			foreach (var import in resolvedImports)
				foreach (var val in import.Value.tasks.Values)
					result.Add (val);

			return result;
		}

		public TaskInfo GetTask (string name)
		{
			TaskInfo task;
			if (tasks.TryGetValue (name, out task))
				return task;

			if (resolvedImports != null) {
				foreach (var import in resolvedImports.Values) {
					if (import.tasks.TryGetValue (name, out task)) {
						return task;
					}
				}
			}

			return null;
		}

		public IEnumerable<PropertyInfo> GetProperties ()
		{
			var result = new HashSet<PropertyInfo> (Builtins.Properties.Values);

			foreach (var p in properties)
				result.Add (p.Value);

			if (resolvedImports != null) {
				foreach (var import in resolvedImports) {
					foreach (var p in import.Value.properties) {
						result.Add (p.Value);
					}
				}
			}

			return result;
		}

		//by convention, properties and items starting with an underscore are "private"
		static bool NotPrivate (string arg)
		{
			return arg [0] != '_';
		}

		public static async Task<MSBuildResolveContext> Create (MSBuildParsedDocument doc, MSBuildResolveContext previous)
		{
			var ctx = new MSBuildResolveContext ();

			ctx.ToolsVersion = doc.ToolsVersion;

			if (previous != null && previous.ToolsVersion == ctx.ToolsVersion) {
				ctx.importEvalCtx = previous.importEvalCtx;
			} else {
				ctx.importEvalCtx = CreateImportEvalCtx (ctx.ToolsVersion);
			}

			ctx.Populate (doc.XDocument);

			if (previous != null && doc.HasErrors) {
				ctx.MergeFrom (previous);
			}

			ctx.resolvedImports = new Dictionary<string, MSBuildResolveContext> ();
			if (previous != null && previous.ToolsVersion == ctx.ToolsVersion && previous.resolvedImports != null) {
				await ctx.ResolveImports (ctx.imports, previous);
			} else {
				await ctx.ResolveImports (ctx.imports, null);
			}

			return ctx;
		}

		async Task ResolveImports (HashSet<string> imports, MSBuildResolveContext previous, string basePath = null)
		{
			foreach (var import in imports) {
				string filename = importEvalCtx.Evaluate (import);

				if (basePath != null) {
					filename = Path.Combine (basePath, filename);
				}

				if (!Platform.IsWindows) {
					filename = filename.Replace ('\\', '/');
				}

				if (!File.Exists (filename)) {
					continue;
				}

				var bp = Path.GetDirectoryName (filename);

				if (previous != null && previous.ToolsVersion == ToolsVersion && previous.resolvedImports != null) {
					MSBuildResolveContext prevImport;
					//ignore mtimes on imports for now // && prevImport.TimeStampUtc <= File.GetLastWriteTimeUtc (filename)
					if (previous.resolvedImports.TryGetValue (filename, out prevImport)) {
						resolvedImports [filename] = prevImport;
						await ResolveImports (prevImport.imports, previous, bp);
						continue;
					}
				}

				if (resolvedImports.ContainsKey (filename)) {
					continue;
				}

				var parseOptions = new Ide.TypeSystem.ParseOptions {
					FileName = filename,
					Content = TextFileProvider.Instance.GetReadOnlyTextEditorData (filename)
				};

				var doc = (XmlParsedDocument)await TypeSystemService.ParseFile (parseOptions, "application/xml");
				var ctx = new MSBuildResolveContext ();
				if (doc.XDocument != null) {
					ctx.Populate (doc.XDocument);
				}
				resolvedImports [filename] = ctx;
				await ResolveImports (ctx.imports, previous, bp);
			}
		}

		void MergeFrom (MSBuildResolveContext fromCtx)
		{
			foreach (var fromItem in fromCtx.items) {
				ItemInfo toItem;
				if (items.TryGetValue (fromItem.Key, out toItem)) {
					foreach (var fromMeta in fromItem.Value.Metadata) {
						if (toItem.Metadata.ContainsKey (fromMeta.Key)) {
							toItem.Metadata [fromMeta.Key] = fromMeta.Value;
						}
					}
				} else {
					items [fromItem.Key] = fromItem.Value;
				}
			}

			foreach (var fromTask in fromCtx.tasks) {
				TaskInfo toTask;
				if (tasks.TryGetValue (fromTask.Key, out toTask)) {
					foreach (var fromParams in fromTask.Value.Parameters) {
						toTask.Parameters.Add (fromParams);
					}
				} else {
					tasks [fromTask.Key] = fromTask.Value;
				}
			}

			foreach (var fromProp in fromCtx.properties) {
				if (!properties.ContainsKey (fromProp.Key)) {
					properties[fromProp.Key] = fromProp.Value;
				}
			}

			foreach (var imp in fromCtx.imports) {
				imports.Add (imp);
			}
		}

		void Populate (XDocument doc)
		{
			var project = doc.Nodes.OfType<XElement> ().FirstOrDefault (x => x.Name == xnProject);
			if (project == null)
				return;
			var pel = MSBuildElement.Get ("Project");
			foreach (var el in project.Nodes.OfType<XElement> ())
				Populate (el, pel);
		}

		void Populate (XElement el, MSBuildElement parent)
		{
			if (el.Name.Prefix != null)
				return;

			var condition = el.Attributes.Get (new XName ("Condition"), true);
			if (condition != null) {
				ExtractReferences (condition);
			}

			var name = el.Name.Name;

			var msel = MSBuildElement.Get (name, parent);
			if (msel == null)
				return;

			if (!msel.IsSpecial) {
				foreach (var child in el.Nodes.OfType<XElement> ())
					Populate (child, msel);
			}

			switch (msel.Kind) {
			case MSBuildKind.Import:
				var import = el.Attributes [xnProject];
				if (import != null && !string.IsNullOrEmpty (import.Value)) {
					imports.Add (import.Value);
				}
				return;
			case MSBuildKind.Item:
				ItemInfo item;
				if (!items.TryGetValue (name, out item))
					items [name] = item = new ItemInfo (name, null);
				foreach (var metadata in el.Nodes.OfType<XElement> ()) {
					var metaName = metadata.Name.Name;
					if (!metadata.Name.HasPrefix && !item.Metadata.ContainsKey ((string)metaName))
						item.Metadata.Add ((string)metaName, new MetadataInfo ((string)metaName, null));
				}
				return;
			case MSBuildKind.Task:
				TaskInfo task;
				if (!tasks.TryGetValue (name, out task))
					tasks [name] = task = new TaskInfo (name, null);
				foreach (var att in el.Attributes) {
					if (!att.Name.HasPrefix)
						task.Parameters.Add (att.Name.Name);
					ExtractReferences (att);
				}
				return;
			case MSBuildKind.Property:
				if (!properties.ContainsKey (name)) {
					properties.Add (name, new PropertyInfo (name, null));
				}
				return;
			case MSBuildKind.Target:
				foreach (var att in el.Attributes) {
					switch (att.Name.Name) {
					case "Inputs":
					case "Outputs":
					case "DependOnTargets":
						ExtractReferences (att);
						break;
					}
				}
				return;
			}
		}

		void ExtractReferences (XAttribute att)
		{
			if (!string.IsNullOrEmpty (att.Value))
				ExtractReferences (att.Value);
		}

		void ExtractReferences (string value)
		{
			var expr = new Expression ();
			//TODO: check options
			expr.Parse (value, ExpressionParser.ParseOptions.AllowItemsMetadataAndSplit);

			ExtractReferences (expr);
		}

		void ExtractReferences (Expression expr)
		{
			foreach (var val in expr.Collection) {
				ExtractReferences (val);
			}
		}

		void ExtractReferences (object val)
		{
			//TODO: InvalidExpressionError

			var pr = val as PropertyReference;
			if (pr != null) {
				if (!properties.ContainsKey (pr.Name) && !Builtins.Properties.ContainsKey (pr.Name)) {
					properties.Add (pr.Name, new PropertyInfo (pr.Name, null));
				}
				return;
			}

			var ir = val as ItemReference;
			if (ir != null) {
				ItemInfo item;
				if (!items.TryGetValue (ir.ItemName, out item))
					items [ir.ItemName] = item = new ItemInfo (ir.ItemName, null);
				if (ir.Transform != null)
					ExtractReferences (ir.Transform);
				return;
			}

			var mr = val as MetadataReference;
			if (mr != null) {
				//TODO: unqualified metadata references
				if (mr.ItemName != null && !Builtins.Metadata.ContainsKey (mr.MetadataName)) {
					ItemInfo item;
					if (!items.TryGetValue (mr.ItemName, out item))
						items [mr.ItemName] = item = new ItemInfo (mr.ItemName, null);
					if (!item.Metadata.ContainsKey (mr.MetadataName)) {
						item.Metadata.Add (mr.MetadataName, new MetadataInfo (mr.MetadataName, null));
					}
				}
				return;
			}

			var mir = val as MemberInvocationReference;
			if (mir != null)
				ExtractReferences (mir.Instance);
		}

		public static MSBuildEvaluationContext CreateImportEvalCtx (string toolsVersion)
		{
			var ctx = new MSBuildEvaluationContext ();
			var runtime = Runtime.SystemAssemblyService.CurrentRuntime;
			ctx.SetPropertyValue ("MSBuildBinPath", runtime.GetMSBuildBinPath (toolsVersion));
			var extPath = runtime.GetMSBuildExtensionsPath ();
			ctx.SetPropertyValue ("MSBuildExtensionsPath", extPath);
			ctx.SetPropertyValue ("MSBuildExtensionsPath32", extPath);
			return ctx;
		}
	}
*/
	class ItemInfo : BaseInfo
	{
		public Dictionary<string,MetadataInfo> Metadata { get; private set; }

		public ItemInfo (string name, string description)
			: base (name, description)
		{
			Metadata = new Dictionary<string, MetadataInfo> ();
		}
	}

	class MetadataInfo : BaseInfo
	{
		public bool WellKnown { get; private set; }

		public MetadataInfo (string name, string description, bool wellKnown = false)
			: base (name, description)
		{
			WellKnown = wellKnown;
		}
	}

	class PropertyInfo : BaseInfo
	{
		public bool Reserved { get; private set; }
		public bool WellKnown { get; private set; }

		public PropertyInfo (string name, string description, bool wellKnown = false, bool reserved = false)
			: base (name, description)
		{
			WellKnown = wellKnown;
			Reserved = reserved;
		}
	}

	class BaseInfo
	{
		public string Name { get; private set; }
		public string Description { get; private set; }

		public BaseInfo (string name, string description)
		{
			Name = name;
			Description = description;
		}

		public override bool Equals (object obj)
		{
			var other = obj as BaseInfo;
			return other != null && string.Equals (Name, other.Name, StringComparison.OrdinalIgnoreCase);
		}

		public override int GetHashCode ()
		{
			return StringComparer.OrdinalIgnoreCase.GetHashCode (Name);
		}
	}

	class TaskInfo : BaseInfo
	{
		public HashSet<string> Parameters { get; internal set; }

		public TaskInfo (string name, string description)
			: base (name, description)
		{
			Parameters = new HashSet<string> ();
		}
	}

	class ParsedImport
	{
		public DateTime TimeStampUtc { get; }
		string Filename { get; }
		XDocument Doc { get; }
		MSBuildResolveContext ResolveContext { get; }

		public ParsedImport (string filename, XDocument doc, DateTime timeStampUtc)
		{
			Filename = filename;
			Doc = doc;
			TimeStampUtc = timeStampUtc;
			ResolveContext = new MSBuildResolveContext ();
		}
	}
}