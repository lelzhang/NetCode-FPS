//
// Engine.cs
//
// Author:
//       Mikayla Hutchinson <m.j.hutchinson@gmail.com>
//
// Copyright (c) 2009 Novell, Inc. (http://www.novell.com)
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
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.CSharp;
using Microsoft.VisualStudio.TextTemplating;
using Random = System.Random;

#if FEATURE_ROSLYN
using Mono.TextTemplating.CodeCompilation;
#endif

namespace Mono.TextTemplating
{
    public class TemplatingEngine :
#if FEATURE_APPDOMAINS
		MarshalByRefObject,
#endif
#pragma warning disable 618
        ITextTemplatingEngine
#pragma warning restore 618
    {
#if FEATURE_ROSLYN
		Func<RuntimeInfo,CodeCompilation.CodeCompiler> createCompilerFunc;
		CodeCompilation.CodeCompiler cachedCompiler;

		internal void SetCompilerFunc (Func<RuntimeInfo,CodeCompilation.CodeCompiler> createCompiler)
		{
			cachedCompiler = null;
			createCompilerFunc = createCompiler;
		}

		CodeCompilation.CodeCompiler GetOrCreateCompiler ()
		{
			if (cachedCompiler == null) {
				var runtime = RuntimeInfo.GetRuntime ();
				if (runtime.Error != null) {
					throw new Exception (runtime.Error);
				}
				cachedCompiler = createCompilerFunc?.Invoke (runtime) ?? new CscCodeCompiler (runtime);
			}
			return cachedCompiler;
		}
#endif

        public string ProcessTemplate(string content, ITextTemplatingEngineHost host)
        {
            using (var tpl = CompileTemplate(content, host))
            {
                return tpl?.Process();
            }
        }

        public string PreprocessTemplate(string content, ITextTemplatingEngineHost host, string className,
            string classNamespace, out string language, out string[] references)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));
            if (host == null)
                throw new ArgumentNullException(nameof(host));
            if (className == null)
                throw new ArgumentNullException(nameof(className));
            language = null;
            references = null;

            var pt = ParsedTemplate.FromText(content, host);
            if (pt.Errors.HasErrors)
            {
                host.LogErrors(pt.Errors);
                return null;
            }

            return PreprocessTemplateInternal(pt, content, host, className, classNamespace, out language,
                out references);
        }

        public string PreprocessTemplate(ParsedTemplate pt, string content, ITextTemplatingEngineHost host,
            string className,
            string classNamespace, out string language, out string[] references, TemplateSettings settings = null)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));
            if (pt == null)
                throw new ArgumentNullException(nameof(pt));
            if (host == null)
                throw new ArgumentNullException(nameof(host));
            if (className == null)
                throw new ArgumentNullException(nameof(className));

            return PreprocessTemplateInternal(pt, content, host, className, classNamespace, out language,
                out references, settings);
        }

        string PreprocessTemplateInternal(ParsedTemplate pt, string content, ITextTemplatingEngineHost host,
            string className,
            string classNamespace, out string language, out string[] references, TemplateSettings settings = null)
        {
            language = null;
            references = null;

            settings = settings ?? GetSettings(host, pt);
            if (pt.Errors.HasErrors)
            {
                host.LogErrors(pt.Errors);
                return null;
            }

            settings.Name = className;
            settings.Namespace = classNamespace;
            settings.IncludePreprocessingHelpers = string.IsNullOrEmpty(settings.Inherits);
            settings.IsPreprocessed = true;
            language = settings.Language;

            var ccu = GenerateCompileUnit(host, content, pt, settings);
            references = ProcessReferences(host, pt, settings).ToArray();

            host.LogErrors(pt.Errors);
            if (pt.Errors.HasErrors)
            {
                return null;
            }

            var options = new CodeGeneratorOptions();
            using (var sw = new StringWriter())
            {
                settings.Provider.GenerateCodeFromCompileUnit(ccu, sw, options);
                return sw.ToString();
            }
        }

        public CompiledTemplate CompileTemplate(string content, ITextTemplatingEngineHost host)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));
            if (host == null)
                throw new ArgumentNullException(nameof(host));

            var pt = ParsedTemplate.FromText(content, host);
            if (pt.Errors.HasErrors)
            {
                host.LogErrors(pt.Errors);
                return null;
            }

            return CompileTemplateInternal(pt, content, host);
        }

        public CompiledTemplate CompileTemplate(
            ParsedTemplate pt,
            string content,
            ITextTemplatingEngineHost host,
            TemplateSettings settings = null)
        {
            if (pt == null)
                throw new ArgumentNullException(nameof(pt));
            if (host == null)
                throw new ArgumentNullException(nameof(host));

            return CompileTemplateInternal(pt, content, host, settings);
        }

        CompiledTemplate CompileTemplateInternal(
            ParsedTemplate pt,
            string content,
            ITextTemplatingEngineHost host,
            TemplateSettings settings = null)
        {
            settings = settings ?? GetSettings(host, pt);
            if (pt.Errors.HasErrors)
            {
                host.LogErrors(pt.Errors);
                return null;
            }

            if (!string.IsNullOrEmpty(settings.Extension))
            {
                host.SetFileExtension(settings.Extension);
            }

            if (settings.Encoding != null)
            {
                //FIXME: when is this called with false?
                host.SetOutputEncoding(settings.Encoding, true);
            }

            var ccu = GenerateCompileUnit(host, content, pt, settings);
            var references = ProcessReferences(host, pt, settings);
            if (pt.Errors.HasErrors)
            {
                host.LogErrors(pt.Errors);
                return null;
            }

            var results = CompileCode(references, settings, ccu);
            if (results.Errors.HasErrors)
            {
                host.LogErrors(pt.Errors);
                host.LogErrors(results.Errors);
                return null;
            }

#if FEATURE_APPDOMAINS
			var domain = host.ProvideTemplatingAppDomain (content);
			var templateClassFullName = string.Concat(settings.Namespace, ".", settings.Name);
			if (domain != null) {
				var type = typeof(CompiledTemplate);
				var obj = domain.CreateInstanceFromAndUnwrap (type.Assembly.Location,
					type.FullName,
					new object[] { host, results, templateClassFullName, settings.Culture, references.ToArray () });
				
				return (CompiledTemplate)obj;
			}
#endif

            return new CompiledTemplate(host, results, settings.GetFullName(), settings.Culture, references.ToArray());
        }

#if FEATURE_ROSLYN
		CompilerResults CompileCode (IEnumerable<string> references, TemplateSettings settings, CodeCompileUnit ccu)
		{
			string sourceText;
			var genOptions = new CodeGeneratorOptions ();
			using (var sw = new StringWriter ()) {
				settings.Provider.GenerateCodeFromCompileUnit (ccu, sw, genOptions);
				sourceText = sw.ToString ();
			}

			// this may throw, so do it before writing source files
			var compiler = GetOrCreateCompiler ();

			var tempFolder = Path.GetTempFileName ();
			File.Delete (tempFolder);
			Directory.CreateDirectory (tempFolder);

			if (settings.Log != null) {
				settings.Log.WriteLine ($"Generating code in '{tempFolder}'");
			}

			var sourceFilename = Path.Combine (tempFolder, settings.Name + "." + settings.Provider.FileExtension);
			File.WriteAllText (sourceFilename, sourceText);

			var args = new CodeCompilerArguments ();
			args.AssemblyReferences.AddRange (references);
			args.Debug = settings.Debug;
			args.SourceFiles.Add (sourceFilename);
			args.AdditionalArguments = settings.CompilerOptions;
			args.OutputPath = Path.Combine (tempFolder, settings.Name + ".dll");
			args.TempDirectory = tempFolder;

			var result = compiler.CompileFile (args, settings.Log, CancellationToken.None).Result;

			var r = new CompilerResults (new TempFileCollection ());
			r.TempFiles.AddFile (sourceFilename, false);

			if (result.ResponseFile != null) {
				r.TempFiles.AddFile (result.ResponseFile, false);
			}

			r.NativeCompilerReturnValue = result.ExitCode;
			r.Output.AddRange (result.Output.ToArray ());
			r.Errors.AddRange (result.Errors.Select (e => new CompilerError (e.Origin ?? "", e.Line, e.Column, e.Code, e.Message) { IsWarning
 = !e.IsError }).ToArray ());

			if (result.Success) {
				r.TempFiles.AddFile (args.OutputPath, true);
				if (args.Debug) {
					r.TempFiles.AddFile (Path.ChangeExtension (args.OutputPath, ".pdb"), true);
				}
				r.PathToAssembly = args.OutputPath;
			} else if (!r.Errors.HasErrors) {
				r.Errors.Add (new CompilerError (null, 0, 0, null, $"The compiler exited with code {result.ExitCode}"));
			}

			if (!args.Debug) {
				r.TempFiles.Delete ();
			}

			return r;
		}
#else
        static CompilerResults CompileCode(IEnumerable<string> references, TemplateSettings settings,
            CodeCompileUnit ccu)
        {
            var pars = new CompilerParameters
            {
                GenerateExecutable = false,
                CompilerOptions = settings.CompilerOptions,
                IncludeDebugInformation = settings.Debug,
                GenerateInMemory = false,
            };

            foreach (var r in references)
                pars.ReferencedAssemblies.Add(r);

            if (settings.Debug)
                pars.TempFiles.KeepFiles = true;
            if (StringUtil.IsNullOrWhiteSpace(pars.CompilerOptions))
                pars.CompilerOptions = "/noconfig";
            else if (!pars.CompilerOptions.Contains("/noconfig"))
                pars.CompilerOptions = "/noconfig " + pars.CompilerOptions;
            return settings.Provider.CompileAssemblyFromDom(pars, ccu);
        }
#endif

        static string[] ProcessReferences(ITextTemplatingEngineHost host, ParsedTemplate pt, TemplateSettings settings)
        {
            var resolved = new Dictionary<string, string>();

            foreach (string assem in settings.Assemblies.Union(host.StandardAssemblyReferences))
            {
                if (resolved.Values.Contains(assem))
                    continue;

                string resolvedAssem = host.ResolveAssemblyReference(assem);

#if UNITY_64
                // 修复路径错误，在Unity中使用<#@ assembly name="$(SolutionDir)\Library\ScriptAssemblies\" #>
                if (resolvedAssem.StartsWith(@"$(SolutionDir)"))
                {
                    resolvedAssem = resolvedAssem.Replace("$(SolutionDir)", Directory.GetCurrentDirectory());
                }

                // UnityEngine.Debug.Log($"解析程序集:{resolvedAssem}");
#endif

                if (!string.IsNullOrEmpty(resolvedAssem))
                {
                    var assemblyName = resolvedAssem;
                    if (File.Exists(resolvedAssem))
                        assemblyName = AssemblyName.GetAssemblyName(resolvedAssem).FullName;
                    resolved[assemblyName] = resolvedAssem;
                }
                else
                {
                    pt.LogError("Could not resolve assembly reference '" + assem + "'");
                    return null;
                }
            }

            return resolved.Values.ToArray();
        }

        public static TemplateSettings GetSettings(ITextTemplatingEngineHost host, ParsedTemplate pt)
        {
            var settings = new TemplateSettings();

            bool relativeLinePragmas = host.GetHostOption("UseRelativeLinePragmas") as bool? ?? false;

            foreach (Directive dt in pt.Directives)
            {
                switch (dt.Name.ToLowerInvariant())
                {
                    case "template":
                        string val = dt.Extract("language");
                        if (val != null)
                            settings.Language = val;
                        val = dt.Extract("debug");
                        if (val != null)
                            settings.Debug = string.Compare(val, "true", StringComparison.OrdinalIgnoreCase) == 0;
                        val = dt.Extract("inherits");
                        if (val != null)
                            settings.Inherits = val;
                        val = dt.Extract("culture");
                        if (val != null)
                        {
                            var culture = System.Globalization.CultureInfo.GetCultureInfo(val);
                            if (culture == null)
                                pt.LogWarning("Could not find culture '" + val + "'", dt.StartLocation);
                            else
                                settings.Culture = culture;
                        }

                        val = dt.Extract("hostspecific");
                        if (val != null)
                        {
                            if (string.Compare(val, "trueFromBase", StringComparison.OrdinalIgnoreCase) == 0)
                            {
                                settings.HostPropertyOnBase = true;
                                settings.HostSpecific = true;
                            }
                            else
                            {
                                settings.HostSpecific =
                                    string.Compare(val, "true", StringComparison.OrdinalIgnoreCase) == 0;
                            }
                        }

                        val = dt.Extract("CompilerOptions");
                        if (val != null)
                        {
                            settings.CompilerOptions = val;
                        }

                        val = dt.Extract("relativeLinePragmas");
                        if (val != null)
                        {
                            relativeLinePragmas = string.Compare(val, "true", StringComparison.OrdinalIgnoreCase) == 0;
                        }

                        val = dt.Extract("linePragmas");
                        if (val != null)
                        {
                            settings.NoLinePragmas =
                                string.Compare(val, "false", StringComparison.OrdinalIgnoreCase) == 0;
                        }

                        val = dt.Extract("visibility");
                        if (val != null)
                        {
                            settings.InternalVisibility =
                                string.Compare(val, "internal", StringComparison.OrdinalIgnoreCase) == 0;
                        }

                        break;

                    case "assembly":
                        string name = dt.Extract("name");
                        if (name == null)
                            pt.LogError("Missing name attribute in assembly directive", dt.StartLocation);
                        else
                            settings.Assemblies.Add(name);
                        break;

                    case "import":
                        string namespac = dt.Extract("namespace");
                        if (namespac == null)
                            pt.LogError("Missing namespace attribute in import directive", dt.StartLocation);
                        else
                            settings.Imports.Add(namespac);
                        break;

                    case "output":
                        settings.Extension = dt.Extract("extension");
                        string encoding = dt.Extract("encoding");
                        if (encoding != null)
                            settings.Encoding = Encoding.GetEncoding(encoding);
                        break;

                    case "include":
                        throw new InvalidOperationException("Include is handled in the parser");

                    case "parameter":
                        AddDirective(settings, host, nameof(ParameterDirectiveProcessor), dt);
                        continue;

                    default:
                        string processorName = dt.Extract("Processor");
                        if (processorName == null)
                            throw new InvalidOperationException("Custom directive '" + dt.Name +
                                                                "' does not specify a processor");

                        AddDirective(settings, host, processorName, dt);
                        continue;
                }

                ComplainExcessAttributes(dt, pt);
            }

            if (host is TemplateGenerator gen)
            {
                settings.HostType = gen.SpecificHostType;
                foreach (var processor in gen.GetAdditionalDirectiveProcessors())
                {
                    settings.DirectiveProcessors[processor.GetType().FullName] = processor;
                }
            }

            if (settings.HostType != null)
            {
                settings.Assemblies.Add(settings.HostType.Assembly.Location);
            }

            //initialize the custom processors
            foreach (var kv in settings.DirectiveProcessors)
            {
                kv.Value.Initialize(host);

                IRecognizeHostSpecific hs;
                if (settings.HostSpecific || (
                    !((IDirectiveProcessor) kv.Value).RequiresProcessingRunIsHostSpecific &&
                    ((hs = kv.Value as IRecognizeHostSpecific) == null || !hs.RequiresProcessingRunIsHostSpecific)))
                    continue;

                settings.HostSpecific = true;
                pt.LogWarning("Directive processor '" + kv.Key + "' requires hostspecific=true, forcing on.");
            }

            foreach (var kv in settings.DirectiveProcessors)
            {
                ((IDirectiveProcessor) kv.Value).SetProcessingRunIsHostSpecific(settings.HostSpecific);
                var hs = kv.Value as IRecognizeHostSpecific;
                if (hs != null)
                    hs.SetProcessingRunIsHostSpecific(settings.HostSpecific);
            }

            if (settings.Name == null)
                settings.Name = "GeneratedTextTransformation";
            if (settings.Namespace == null)
                settings.Namespace = string.Format(typeof(TextTransformation).Namespace + "{0:x}", new Random().Next());

            //resolve the CodeDOM provider
            if (String.IsNullOrEmpty(settings.Language))
            {
                settings.Language = "C#";
            }

            if (settings.Language == "C#v3.5")
            {
                var providerOptions = new Dictionary<string, string>();
                providerOptions.Add("CompilerVersion", "v3.5");
                settings.Provider = new CSharpCodeProvider(providerOptions);
            }
            else
            {
                settings.Provider = CodeDomProvider.CreateProvider(settings.Language);
            }

            if (settings.Provider == null)
            {
                pt.LogError("A provider could not be found for the language '" + settings.Language + "'");
                return settings;
            }

            settings.RelativeLinePragmas = relativeLinePragmas;

            return settings;
        }

        public static string IndentSnippetText(CodeDomProvider provider, string text, string indent)
        {
            if (provider is CSharpCodeProvider)
                return IndentSnippetText(text, indent);
            return text;
        }

        public static string IndentSnippetText(string text, string indent)
        {
            var builder = new StringBuilder(text.Length);
            builder.Append(indent);
            int lastNewline = 0;
            for (int i = 0; i < text.Length - 1; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (text[i + 1] == '\n')
                    {
                        i++;
                        if (i == text.Length - 1)
                            break;
                    }
                }
                else if (c != '\n' || text[i + 1] == '\n')
                {
                    continue;
                }

                i++;
                int len = i - lastNewline;
                if (len > 0)
                {
                    builder.Append(text, lastNewline, i - lastNewline);
                }

                builder.Append(indent);
                lastNewline = i;
            }

            if (lastNewline > 0)
                builder.Append(text, lastNewline, text.Length - lastNewline);
            else
                builder.Append(text);
            return builder.ToString();
        }

        static void AddDirective(TemplateSettings settings, ITextTemplatingEngineHost host, string processorName,
            Directive directive)
        {
            if (!settings.DirectiveProcessors.TryGetValue(processorName, out IDirectiveProcessor processor))
            {
                switch (processorName)
                {
                    case "ParameterDirectiveProcessor":
                        processor = new ParameterDirectiveProcessor();
                        break;
                    default:
                        Type processorType = host.ResolveDirectiveProcessor(processorName);
                        processor = (IDirectiveProcessor) Activator.CreateInstance(processorType);
                        break;
                }

                if (!processor.IsDirectiveSupported(directive.Name))
                    throw new InvalidOperationException("Directive processor '" + processorName +
                                                        "' does not support directive '" + directive.Name + "'");

                settings.DirectiveProcessors[processorName] = processor;
            }

            settings.CustomDirectives.Add(new CustomDirective(processorName, directive));
        }

        static bool ComplainExcessAttributes(Directive dt, ParsedTemplate pt)
        {
            if (dt.Attributes.Count == 0)
                return false;
            var sb = new StringBuilder("Unknown attributes ");
            bool first = true;
            foreach (string key in dt.Attributes.Keys)
            {
                if (!first)
                {
                    sb.Append(", ");
                }
                else
                {
                    first = false;
                }

                sb.Append(key);
            }

            sb.Append(" found in ");
            sb.Append(dt.Name);
            sb.Append(" directive.");
            pt.LogWarning(sb.ToString(), dt.StartLocation);
            return false;
        }

        static void ProcessDirectives(string content, ParsedTemplate pt, TemplateSettings settings)
        {
            foreach (var processor in settings.DirectiveProcessors.Values)
            {
                processor.StartProcessingRun(settings.Provider, content, pt.Errors);
            }

            foreach (var dt in settings.CustomDirectives)
            {
                var processor = settings.DirectiveProcessors[dt.ProcessorName];
                processor.ProcessDirective(dt.Directive.Name, dt.Directive.Attributes);
            }

            foreach (var processor in settings.DirectiveProcessors.Values)
            {
                processor.FinishProcessingRun();

                var imports = processor.GetImportsForProcessingRun();
                if (imports != null)
                    settings.Imports.UnionWith(imports);
                var references = processor.GetReferencesForProcessingRun();
                if (references != null)
                    settings.Assemblies.UnionWith(references);
            }
        }

        public static CodeCompileUnit GenerateCompileUnit(ITextTemplatingEngineHost host, string content,
            ParsedTemplate pt, TemplateSettings settings)
        {
            ProcessDirectives(content, pt, settings);

            string baseDirectory = Path.GetDirectoryName(host.TemplateFile);

            //prep the compile unit
            var ccu = new CodeCompileUnit();
            var namespac = string.IsNullOrEmpty(settings.Namespace)
                ? new CodeNamespace()
                : new CodeNamespace(settings.Namespace);
            ccu.Namespaces.Add(namespac);

            foreach (string ns in settings.Imports.Union(host.StandardImports))
                namespac.Imports.Add(new CodeNamespaceImport(ns));

            //prep the type
            var type = new CodeTypeDeclaration(settings.Name)
            {
                IsPartial = true
            };
            if (settings.InternalVisibility)
            {
                type.TypeAttributes = (type.TypeAttributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NotPublic;
            }

            if (!string.IsNullOrEmpty(settings.Inherits))
            {
                type.BaseTypes.Add(new CodeTypeReference(settings.Inherits));
            }
            else if (!settings.IncludePreprocessingHelpers)
            {
                type.BaseTypes.Add(TypeRef<TextTransformation>());
            }
            else
            {
                type.BaseTypes.Add(new CodeTypeReference(settings.Name + "Base"));
            }

            namespac.Types.Add(type);

            //prep the transform method
            var transformMeth = new CodeMemberMethod
            {
                Name = "TransformText",
                ReturnType = new CodeTypeReference(typeof(String)),
                Attributes = MemberAttributes.Public,
            };
            if (!settings.IncludePreprocessingHelpers)
                transformMeth.Attributes |= MemberAttributes.Override;

            transformMeth.Statements.Add(new CodeAssignStatement(
                new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "GenerationEnvironment"),
                new CodePrimitiveExpression(null)));

            CodeExpression toStringHelper;
            if (settings.IsPreprocessed)
            {
                toStringHelper =
                    new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "ToStringHelper");
            }
            else
            {
                toStringHelper = new CodeTypeReferenceExpression(
                    new CodeTypeReference(typeof(ToStringHelper), CodeTypeReferenceOptions.GlobalReference));
            }

            //method references that will need to be used multiple times
            var writeMeth = new CodeMethodReferenceExpression(new CodeThisReferenceExpression(), "Write");
            var toStringMeth = new CodeMethodReferenceExpression(toStringHelper, "ToStringWithCulture");
            bool helperMode = false;

            //build the code from the segments
            foreach (TemplateSegment seg in pt.Content)
            {
                CodeStatement st = null;
                CodeLinePragma location = null;
                if (!settings.NoLinePragmas)
                {
                    var f = seg.StartLocation.FileName ?? host.TemplateFile;
                    if (settings.RelativeLinePragmas)
                        f = FileUtil.AbsoluteToRelativePath(baseDirectory, f).Replace('\\', '/');
                    location = new CodeLinePragma(f, seg.StartLocation.Line);
                }

                switch (seg.Type)
                {
                    case SegmentType.Block:
                        if (helperMode)
                            //TODO: are blocks permitted after helpers?
                            pt.LogError("Blocks are not permitted after helpers", seg.TagStartLocation);
                        st = new CodeSnippetStatement(seg.Text);
                        break;
                    case SegmentType.Expression:
                        st = new CodeExpressionStatement(
                            new CodeMethodInvokeExpression(writeMeth,
                                new CodeMethodInvokeExpression(toStringMeth, new CodeSnippetExpression(seg.Text))));
                        break;
                    case SegmentType.Content:
                        st = new CodeExpressionStatement(
                            new CodeMethodInvokeExpression(writeMeth, new CodePrimitiveExpression(seg.Text)));
                        break;
                    case SegmentType.Helper:
                        if (!string.IsNullOrEmpty(seg.Text))
                            type.Members.Add(CreateSnippetMember(seg.Text, location));
                        helperMode = true;
                        break;
                    default:
                        throw new InvalidOperationException();
                }

                if (st != null)
                {
                    if (helperMode)
                    {
                        //convert the statement into a snippet member and attach it to the top level type
                        //TODO: is there a way to do this for languages that use indentation for blocks, e.g. python?
                        using (var writer = new StringWriter())
                        {
                            settings.Provider.GenerateCodeFromStatement(st, writer, null);
                            var text = writer.ToString();
                            if (!string.IsNullOrEmpty(text))
                                type.Members.Add(CreateSnippetMember(text, location));
                        }
                    }
                    else
                    {
                        st.LinePragma = location;
                        transformMeth.Statements.Add(st);
                        continue;
                    }
                }
            }

            //complete the transform method
            transformMeth.Statements.Add(new CodeMethodReturnStatement(
                new CodeMethodInvokeExpression(
                    new CodePropertyReferenceExpression(
                        new CodeThisReferenceExpression(),
                        "GenerationEnvironment"),
                    "ToString")));
            type.Members.Add(transformMeth);

            //class code and attributes from processors
            foreach (var processor in settings.DirectiveProcessors.Values)
            {
                string classCode = processor.GetClassCodeForProcessingRun();
                if (!string.IsNullOrEmpty(classCode))
                    type.Members.Add(CreateSnippetMember(classCode));
                var atts = processor.GetTemplateClassCustomAttributes();
                if (atts != null)
                {
                    if (type.CustomAttributes == null)
                        type.CustomAttributes = new CodeAttributeDeclarationCollection();
                    type.CustomAttributes.AddRange(atts);
                }
            }

            //generate the Host property if needed
            if (settings.HostSpecific && !settings.HostPropertyOnBase)
            {
                GenerateHostProperty(type, settings.HostType);
            }

            GenerateInitializationMethod(type, settings);

            if (settings.IncludePreprocessingHelpers)
            {
                var baseClass = new CodeTypeDeclaration(settings.Name + "Base");
                GenerateProcessingHelpers(baseClass, settings);
                AddToStringHelper(baseClass, settings);
                namespac.Types.Add(baseClass);
            }

            return ccu;
        }

        static CodeSnippetTypeMember CreateSnippetMember(string value, CodeLinePragma location = null)
        {
            //HACK: workaround for code generator not indenting first line of member snippet when inserting into class
            const string indent = "\n        ";
            if (!char.IsWhiteSpace(value[0]))
                value = indent + value;

            return new CodeSnippetTypeMember(value)
            {
                LinePragma = location
            };
        }

        static void GenerateHostProperty(CodeTypeDeclaration type, Type hostType)
        {
            hostType = hostType ?? typeof(ITextTemplatingEngineHost);

            var hostTypeRef = new CodeTypeReference(hostType, CodeTypeReferenceOptions.GlobalReference);
            var hostField = new CodeMemberField(hostTypeRef, "hostValue");
            hostField.Attributes = (hostField.Attributes & ~MemberAttributes.AccessMask) | MemberAttributes.Private;
            type.Members.Add(hostField);

            var hostProp = GenerateGetterSetterProperty("Host", hostField);
            hostProp.Attributes = MemberAttributes.Public | MemberAttributes.Final;
            type.Members.Add(hostProp);
        }

        static void GenerateInitializationMethod(CodeTypeDeclaration type, TemplateSettings settings)
        {
            //initialization method
            var initializeMeth = new CodeMemberMethod
            {
                Name = "Initialize",
                ReturnType = new CodeTypeReference(typeof(void), CodeTypeReferenceOptions.GlobalReference),
                Attributes = MemberAttributes.Public
            };
            if (!settings.IncludePreprocessingHelpers)
                initializeMeth.Attributes |= MemberAttributes.Override;

            //if preprocessed, pass the extension and encoding to the host
            if (settings.IsPreprocessed && settings.HostSpecific)
            {
                var hostProp = new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "Host");
                var statements = new List<CodeStatement>();

                if (!string.IsNullOrEmpty(settings.Extension))
                {
                    statements.Add(new CodeExpressionStatement(new CodeMethodInvokeExpression(
                        hostProp,
                        "SetFileExtension",
                        new CodePrimitiveExpression(settings.Extension)
                    )));
                }

                if (settings.Encoding != null)
                {
                    statements.Add(new CodeExpressionStatement(new CodeMethodInvokeExpression(
                        hostProp,
                        "SetOutputEncoding",
                        new CodeMethodInvokeExpression(
                            new CodeTypeReferenceExpression(typeof(Encoding)),
                            "GetEncoding",
                            new CodePrimitiveExpression(settings.Encoding.CodePage),
                            new CodePrimitiveExpression(true)
                        )
                    )));
                }

                if (statements.Count > 0)
                {
                    initializeMeth.Statements.Add(new CodeConditionStatement(
                        new CodeBinaryOperatorExpression(
                            hostProp,
                            CodeBinaryOperatorType.IdentityInequality,
                            new CodePrimitiveExpression(null)
                        ),
                        statements.ToArray()
                    ));
                }
            }

            //pre-init code from processors
            foreach (var processor in settings.DirectiveProcessors.Values)
            {
                string code = processor.GetPreInitializationCodeForProcessingRun();
                if (code != null)
                    initializeMeth.Statements.Add(new CodeSnippetStatement(code));
            }

            //base call
            if (!settings.IncludePreprocessingHelpers)
            {
                initializeMeth.Statements.Add(
                    new CodeMethodInvokeExpression(
                        new CodeMethodReferenceExpression(
                            new CodeBaseReferenceExpression(),
                            "Initialize")));
            }

            //post-init code from processors
            foreach (var processor in settings.DirectiveProcessors.Values)
            {
                string code = processor.GetPostInitializationCodeForProcessingRun();
                if (code != null)
                    initializeMeth.Statements.Add(new CodeSnippetStatement(code));
            }

            type.Members.Add(initializeMeth);
        }

        static void GenerateProcessingHelpers(CodeTypeDeclaration type, TemplateSettings settings)
        {
            var thisRef = new CodeThisReferenceExpression();
            var sbTypeRef = TypeRef<StringBuilder>();

            var sessionField = PrivateField(TypeRef<IDictionary<string, object>>(), "session");
            var sessionProp = GenerateGetterSetterProperty("Session", sessionField);
            sessionProp.Attributes = MemberAttributes.Public;

            var builderField = PrivateField(sbTypeRef, "builder");
            var builderFieldRef = new CodeFieldReferenceExpression(thisRef, builderField.Name);

            var generationEnvironmentProp = GenerateGetterSetterProperty("GenerationEnvironment", builderField);
            AddPropertyGetterInitializationIfFieldIsNull(generationEnvironmentProp, builderFieldRef,
                TypeRef<StringBuilder>());

            type.Members.Add(builderField);
            type.Members.Add(sessionField);
            type.Members.Add(sessionProp);
            type.Members.Add(generationEnvironmentProp);

            AddErrorHelpers(type);
            AddIndentHelpers(type);
            AddWriteHelpers(type);
        }

        static void AddPropertyGetterInitializationIfFieldIsNull(CodeMemberProperty property,
            CodeFieldReferenceExpression fieldRef, CodeTypeReference typeRef)
        {
            var fieldInit = FieldInitializationIfNull(fieldRef, typeRef);
            property.GetStatements.Insert(0, fieldInit);
        }

        static CodeConditionStatement FieldInitializationIfNull(CodeExpression fieldRef, CodeTypeReference typeRef)
        {
            return new CodeConditionStatement(
                new CodeBinaryOperatorExpression(fieldRef,
                    CodeBinaryOperatorType.ValueEquality, new CodePrimitiveExpression(null)),
                new CodeAssignStatement(fieldRef, new CodeObjectCreateExpression(typeRef)));
        }

        static void AddErrorHelpers(CodeTypeDeclaration type)
        {
            var cecTypeRef = TypeRef<CompilerErrorCollection>();
            var thisRef = new CodeThisReferenceExpression();
            var stringTypeRef = TypeRef<string>();
            var nullPrim = new CodePrimitiveExpression(null);
            var minusOnePrim = new CodePrimitiveExpression(-1);

            var errorsField = PrivateField(cecTypeRef, "errors");
            var errorsFieldRef = new CodeFieldReferenceExpression(thisRef, errorsField.Name);

            var errorsProp = GenerateGetterProperty("Errors", errorsField);
            errorsProp.Attributes = MemberAttributes.Family | MemberAttributes.Final;
            errorsProp.GetStatements.Insert(0,
                FieldInitializationIfNull(errorsFieldRef, TypeRef<CompilerErrorCollection>()));

            var errorsPropRef = new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "Errors");

            var compilerErrorTypeRef = TypeRef<CompilerError>();
            var errorMeth = new CodeMemberMethod
            {
                Name = "Error",
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
            };
            errorMeth.Parameters.Add(new CodeParameterDeclarationExpression(stringTypeRef, "message"));
            errorMeth.Statements.Add(new CodeMethodInvokeExpression(errorsPropRef, "Add",
                new CodeObjectCreateExpression(compilerErrorTypeRef, nullPrim, minusOnePrim, minusOnePrim, nullPrim,
                    new CodeArgumentReferenceExpression("message"))));

            var warningMeth = new CodeMemberMethod
            {
                Name = "Warning",
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
            };
            warningMeth.Parameters.Add(new CodeParameterDeclarationExpression(stringTypeRef, "message"));
            warningMeth.Statements.Add(new CodeVariableDeclarationStatement(compilerErrorTypeRef, "val",
                new CodeObjectCreateExpression(compilerErrorTypeRef, nullPrim, minusOnePrim, minusOnePrim, nullPrim,
                    new CodeArgumentReferenceExpression("message"))));
            warningMeth.Statements.Add(new CodeAssignStatement(new CodePropertyReferenceExpression(
                new CodeVariableReferenceExpression("val"), "IsWarning"), new CodePrimitiveExpression(true)));
            warningMeth.Statements.Add(new CodeMethodInvokeExpression(errorsPropRef, "Add",
                new CodeVariableReferenceExpression("val")));

            type.Members.Add(errorsField);
            type.Members.Add(errorMeth);
            type.Members.Add(warningMeth);
            type.Members.Add(errorsProp);
        }

        static void AddIndentHelpers(CodeTypeDeclaration type)
        {
            var stringTypeRef = TypeRef<string>();
            var thisRef = new CodeThisReferenceExpression();
            var zeroPrim = new CodePrimitiveExpression(0);
            var stringEmptyRef =
                new CodeFieldReferenceExpression(new CodeTypeReferenceExpression(stringTypeRef), "Empty");
            var intTypeRef = TypeRef<int>();
            var stackIntTypeRef = TypeRef<Stack<int>>();

            var indentsField = PrivateField(stackIntTypeRef, "indents");
            var indentsFieldRef = new CodeFieldReferenceExpression(thisRef, indentsField.Name);

            var indentsProp = GenerateGetterProperty("Indents", indentsField);
            indentsProp.Attributes = MemberAttributes.Private;
            AddPropertyGetterInitializationIfFieldIsNull(indentsProp, indentsFieldRef, TypeRef<Stack<int>>());

            var indentsPropRef = new CodeFieldReferenceExpression(thisRef, indentsProp.Name);

            var currentIndentField = PrivateField(stringTypeRef, "currentIndent");
            currentIndentField.InitExpression = stringEmptyRef;
            var currentIndentFieldRef = new CodeFieldReferenceExpression(thisRef, currentIndentField.Name);

            var popIndentMeth = new CodeMemberMethod
            {
                Name = "PopIndent",
                ReturnType = stringTypeRef,
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
            };
            popIndentMeth.Statements.Add(new CodeConditionStatement(
                new CodeBinaryOperatorExpression(new CodePropertyReferenceExpression(indentsPropRef, "Count"),
                    CodeBinaryOperatorType.ValueEquality, zeroPrim),
                new CodeMethodReturnStatement(stringEmptyRef)));
            popIndentMeth.Statements.Add(new CodeVariableDeclarationStatement(intTypeRef, "lastPos",
                new CodeBinaryOperatorExpression(
                    new CodePropertyReferenceExpression(currentIndentFieldRef, "Length"),
                    CodeBinaryOperatorType.Subtract,
                    new CodeMethodInvokeExpression(indentsPropRef, "Pop"))));
            popIndentMeth.Statements.Add(new CodeVariableDeclarationStatement(stringTypeRef, "last",
                new CodeMethodInvokeExpression(currentIndentFieldRef, "Substring",
                    new CodeVariableReferenceExpression("lastPos"))));
            popIndentMeth.Statements.Add(new CodeAssignStatement(currentIndentFieldRef,
                new CodeMethodInvokeExpression(currentIndentFieldRef, "Substring", zeroPrim,
                    new CodeVariableReferenceExpression("lastPos"))));
            popIndentMeth.Statements.Add(new CodeMethodReturnStatement(new CodeVariableReferenceExpression("last")));

            var pushIndentMeth = new CodeMemberMethod
            {
                Name = "PushIndent",
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
            };
            pushIndentMeth.Parameters.Add(new CodeParameterDeclarationExpression(stringTypeRef, "indent"));
            pushIndentMeth.Statements.Add(new CodeMethodInvokeExpression(indentsPropRef, "Push",
                new CodePropertyReferenceExpression(new CodeArgumentReferenceExpression("indent"), "Length")));
            pushIndentMeth.Statements.Add(new CodeAssignStatement(currentIndentFieldRef,
                new CodeBinaryOperatorExpression(currentIndentFieldRef, CodeBinaryOperatorType.Add,
                    new CodeArgumentReferenceExpression("indent"))));

            var clearIndentMeth = new CodeMemberMethod
            {
                Name = "ClearIndent",
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
            };
            clearIndentMeth.Statements.Add(new CodeAssignStatement(currentIndentFieldRef, stringEmptyRef));
            clearIndentMeth.Statements.Add(new CodeMethodInvokeExpression(indentsPropRef, "Clear"));

            var currentIndentProp = GenerateGetterProperty("CurrentIndent", currentIndentField);
            type.Members.Add(currentIndentField);
            type.Members.Add(indentsField);
            type.Members.Add(popIndentMeth);
            type.Members.Add(pushIndentMeth);
            type.Members.Add(clearIndentMeth);
            type.Members.Add(currentIndentProp);
            type.Members.Add(indentsProp);
        }

        static void AddWriteHelpers(CodeTypeDeclaration type)
        {
            var stringTypeRef = TypeRef<string>();
            var thisRef = new CodeThisReferenceExpression();
            var genEnvPropRef = new CodePropertyReferenceExpression(thisRef, "GenerationEnvironment");
            var currentIndentFieldRef = new CodeFieldReferenceExpression(thisRef, "currentIndent");

            var textToAppendParam = new CodeParameterDeclarationExpression(stringTypeRef, "textToAppend");
            var formatParam = new CodeParameterDeclarationExpression(stringTypeRef, "format");
            var argsParam = new CodeParameterDeclarationExpression(TypeRef<object[]>(), "args");
            argsParam.CustomAttributes.Add(new CodeAttributeDeclaration(TypeRef<ParamArrayAttribute>()));

            var textToAppendParamRef = new CodeArgumentReferenceExpression("textToAppend");
            var formatParamRef = new CodeArgumentReferenceExpression("format");
            var argsParamRef = new CodeArgumentReferenceExpression("args");

            var writeMeth = new CodeMemberMethod
            {
                Name = "Write",
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
            };
            writeMeth.Parameters.Add(textToAppendParam);
            writeMeth.Statements.Add(new CodeMethodInvokeExpression(genEnvPropRef, "Append",
                new CodeArgumentReferenceExpression("textToAppend")));

            var writeArgsMeth = new CodeMemberMethod
            {
                Name = "Write",
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
            };
            writeArgsMeth.Parameters.Add(formatParam);
            writeArgsMeth.Parameters.Add(argsParam);
            writeArgsMeth.Statements.Add(new CodeMethodInvokeExpression(genEnvPropRef, "AppendFormat", formatParamRef,
                argsParamRef));

            var writeLineMeth = new CodeMemberMethod
            {
                Name = "WriteLine",
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
            };
            writeLineMeth.Parameters.Add(textToAppendParam);
            writeLineMeth.Statements.Add(new CodeMethodInvokeExpression(genEnvPropRef, "Append",
                currentIndentFieldRef));
            writeLineMeth.Statements.Add(new CodeMethodInvokeExpression(genEnvPropRef, "AppendLine",
                textToAppendParamRef));

            var writeLineArgsMeth = new CodeMemberMethod
            {
                Name = "WriteLine",
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
            };
            writeLineArgsMeth.Parameters.Add(formatParam);
            writeLineArgsMeth.Parameters.Add(argsParam);
            writeLineArgsMeth.Statements.Add(new CodeMethodInvokeExpression(genEnvPropRef, "Append",
                currentIndentFieldRef));
            writeLineArgsMeth.Statements.Add(new CodeMethodInvokeExpression(genEnvPropRef, "AppendFormat",
                formatParamRef, argsParamRef));
            writeLineArgsMeth.Statements.Add(new CodeMethodInvokeExpression(genEnvPropRef, "AppendLine"));

            type.Members.Add(writeMeth);
            type.Members.Add(writeArgsMeth);
            type.Members.Add(writeLineMeth);
            type.Members.Add(writeLineArgsMeth);
        }

        static void AddToStringHelper(CodeTypeDeclaration type, TemplateSettings settings)
        {
            var helperCls = new CodeTypeDeclaration("ToStringInstanceHelper")
            {
                IsClass = true,
                TypeAttributes = TypeAttributes.NestedPublic,
            };

            var formatProviderField = PrivateField(TypeRef<IFormatProvider>(), "formatProvider");
            formatProviderField.InitExpression = new CodePropertyReferenceExpression(
                new CodeTypeReferenceExpression(TypeRef<System.Globalization.CultureInfo>()), "InvariantCulture");
            var formatProviderFieldRef =
                new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), formatProviderField.Name);

            var formatProviderProp = GenerateGetterSetterProperty("FormatProvider", formatProviderField);
            MakeSimpleSetterIgnoreNull(formatProviderProp);

            helperCls.Members.Add(formatProviderField);
            helperCls.Members.Add(formatProviderProp);

            var meth = new CodeMemberMethod
            {
                Name = "ToStringWithCulture",
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
                ReturnType = TypeRef<string>(),
            };
            meth.Parameters.Add(new CodeParameterDeclarationExpression(TypeRef<object>(), "objectToConvert"));
            var paramRef = new CodeArgumentReferenceExpression("objectToConvert");

            meth.Statements.Add(NullCheck(paramRef, paramRef.ParameterName));

            var typeLocal = new CodeVariableDeclarationStatement(TypeRef<Type>(), "type",
                new CodeMethodInvokeExpression(paramRef, "GetType"));
            var typeLocalRef = new CodeVariableReferenceExpression(typeLocal.Name);
            meth.Statements.Add(typeLocal);

            var iConvertibleTypeLocal = new CodeVariableDeclarationStatement(TypeRef<Type>(), "iConvertibleType",
                new CodeTypeOfExpression(TypeRef<IConvertible>()));
            var iConvertibleTypeLocalRef = new CodeVariableReferenceExpression(iConvertibleTypeLocal.Name);
            meth.Statements.Add(iConvertibleTypeLocal);

            meth.Statements.Add(new CodeConditionStatement(
                new CodeMethodInvokeExpression(iConvertibleTypeLocalRef, "IsAssignableFrom", typeLocalRef),
                new CodeMethodReturnStatement(new CodeMethodInvokeExpression(
                    new CodeCastExpression(TypeRef<IConvertible>(), paramRef), "ToString", formatProviderFieldRef))));

            var methInfoLocal = new CodeVariableDeclarationStatement(TypeRef<MethodInfo>(), "methInfo",
                new CodeMethodInvokeExpression(typeLocalRef, "GetMethod",
                    new CodePrimitiveExpression("ToString"),
                    new CodeArrayCreateExpression(TypeRef<Type>(), new CodeExpression[] {iConvertibleTypeLocalRef})));
            meth.Statements.Add(methInfoLocal);
            var methInfoLocalRef = new CodeVariableReferenceExpression(methInfoLocal.Name);
            meth.Statements.Add(new CodeConditionStatement(NotNull(methInfoLocalRef),
                new CodeMethodReturnStatement(new CodeCastExpression(TypeRef<string>(),
                    new CodeMethodInvokeExpression(
                        methInfoLocalRef, "Invoke", paramRef,
                        new CodeArrayCreateExpression(TypeRef<object>(),
                            new CodeExpression[] {formatProviderFieldRef}))))));

            meth.Statements.Add(new CodeMethodReturnStatement(new CodeMethodInvokeExpression(paramRef, "ToString")));

            helperCls.Members.Add(meth);


            var helperFieldName = settings.Provider.CreateValidIdentifier("_toStringHelper");
            var helperField = PrivateField(new CodeTypeReference(helperCls.Name), helperFieldName);
            helperField.InitExpression = new CodeObjectCreateExpression(helperField.Type);
            type.Members.Add(helperField);
            type.Members.Add(GenerateGetterProperty("ToStringHelper", helperField));
            type.Members.Add(helperCls);
        }

        #region CodeDom helpers

        static CodeTypeReference TypeRef<T>()
        {
            return new CodeTypeReference(typeof(T), CodeTypeReferenceOptions.GlobalReference);
        }

        static CodeMemberProperty GenerateGetterSetterProperty(string propertyName, CodeMemberField field)
        {
            var prop = new CodeMemberProperty
            {
                Name = propertyName,
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
                Type = field.Type
            };
            var fieldRef = new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), field.Name);
            AddGetter(prop, fieldRef);
            AddSetter(prop, fieldRef);
            return prop;
        }

        static CodeMemberProperty GenerateGetterProperty(string propertyName, CodeMemberField field)
        {
            var prop = new CodeMemberProperty
            {
                Name = propertyName,
                Attributes = MemberAttributes.Public | MemberAttributes.Final,
                HasSet = false,
                Type = field.Type
            };
            var fieldRef = new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), field.Name);
            AddGetter(prop, fieldRef);
            return prop;
        }

        static void AddSetter(CodeMemberProperty property, CodeFieldReferenceExpression fieldRef)
        {
            property.HasSet = true;
            property.SetStatements.Add(new CodeAssignStatement(fieldRef,
                new CodePropertySetValueReferenceExpression()));
        }

        static void AddGetter(CodeMemberProperty property, CodeFieldReferenceExpression fieldRef)
        {
            property.HasGet = true;
            property.GetStatements.Add(new CodeMethodReturnStatement(fieldRef));
        }

        static void MakeGetterLazy(CodeMemberProperty property, CodeFieldReferenceExpression fieldRef,
            CodeExpression initExpression)
        {
            property.GetStatements.Insert(0, new CodeConditionStatement(
                NotNull(fieldRef),
                new CodeAssignStatement(fieldRef, initExpression))
            );
        }

        static void MakeSimpleSetterIgnoreNull(CodeMemberProperty property)
        {
            property.SetStatements[0] = new CodeConditionStatement(
                NotNull(new CodePropertySetValueReferenceExpression()),
                property.SetStatements[0]);
        }

        static CodeStatement NullCheck(CodeExpression expr, string exceptionMessage)
        {
            return new CodeConditionStatement(
                IsNull(expr),
                new CodeThrowExceptionStatement(new CodeObjectCreateExpression(
                    new CodeTypeReference(typeof(ArgumentNullException), CodeTypeReferenceOptions.GlobalReference),
                    new CodePrimitiveExpression(exceptionMessage)))
            );
        }

        static CodeBinaryOperatorExpression NotNull(CodeExpression reference)
        {
            return new CodeBinaryOperatorExpression(reference, CodeBinaryOperatorType.IdentityInequality,
                new CodePrimitiveExpression(null));
        }

        static CodeBinaryOperatorExpression IsNull(CodeExpression reference)
        {
            return new CodeBinaryOperatorExpression(reference, CodeBinaryOperatorType.ValueEquality,
                new CodePrimitiveExpression(null));
        }

        static CodeBinaryOperatorExpression IsFalse(CodeExpression expr)
        {
            return new CodeBinaryOperatorExpression(expr, CodeBinaryOperatorType.ValueEquality,
                new CodePrimitiveExpression(false));
        }

        static CodeBinaryOperatorExpression BooleanAnd(CodeExpression expr1, CodeExpression expr2)
        {
            return new CodeBinaryOperatorExpression(expr1, CodeBinaryOperatorType.BooleanAnd, expr2);
        }

        static CodeStatement ArgNullCheck(CodeExpression value, params CodeExpression[] argNullExcArgs)
        {
            return new CodeConditionStatement(
                new CodeBinaryOperatorExpression(value,
                    CodeBinaryOperatorType.ValueEquality, new CodePrimitiveExpression(null)),
                new CodeThrowExceptionStatement(new CodeObjectCreateExpression(typeof(ArgumentNullException),
                    argNullExcArgs)));
        }

        static CodeMemberField PrivateField(CodeTypeReference typeRef, string name)
        {
            return new CodeMemberField(typeRef, name)
            {
                Attributes = MemberAttributes.Private
            };
        }

        #endregion

        //HACK: older versions of Mono don't implement GenerateCodeFromMember
        // We have a workaround via reflection. First attempt to reflect the members we need to work around it.
        // If they don't exist, we should be running on a version where it's fixed.
        static readonly bool useMonoHack = InitializeMonoHack();
        static MethodInfo cgFieldGen, cgPropGen, cgMethGen;
        static Action<CodeGenerator, StringWriter, CodeGeneratorOptions> initializeCodeGenerator;

        /// <summary>
        /// An implementation of CodeDomProvider.GenerateCodeFromMember that works on Mono.
        /// </summary>
        public static void GenerateCodeFromMembers(CodeDomProvider provider, CodeGeneratorOptions options,
            StringWriter sw, IEnumerable<CodeTypeMember> members)
        {
            if (!useMonoHack)
            {
                foreach (CodeTypeMember member in members)
                    provider.GenerateCodeFromMember(member, sw, options);
                return;
            }

#pragma warning disable 0618
            var generator = (CodeGenerator) provider.CreateGenerator();
#pragma warning restore 0618
            var dummy = new CodeTypeDeclaration("Foo");

            foreach (CodeTypeMember member in members)
            {
                if (member is CodeMemberField f)
                {
                    initializeCodeGenerator(generator, sw, options);
                    cgFieldGen.Invoke(generator, new object[] {f});
                    continue;
                }

                if (member is CodeMemberProperty p)
                {
                    initializeCodeGenerator(generator, sw, options);
                    cgPropGen.Invoke(generator, new object[] {p, dummy});
                    continue;
                }

                if (member is CodeMemberMethod m)
                {
                    initializeCodeGenerator(generator, sw, options);
                    cgMethGen.Invoke(generator, new object[] {m, dummy});
                    continue;
                }
            }
        }

        static bool InitializeMonoHack()
        {
            if (Type.GetType("Mono.Runtime") == null)
            {
                return false;
            }

            var cgType = typeof(CodeGenerator);

            var cgInit = cgType.GetMethod("InitOutput", BindingFlags.NonPublic | BindingFlags.Instance);
            if (cgInit != null)
            {
                initializeCodeGenerator =
                    new Action<CodeGenerator, StringWriter, CodeGeneratorOptions>((generator, sw, options) =>
                    {
                        cgInit.Invoke(generator, new object[] {sw, options});
                    });
            }
            else
            {
                var cgOptions = cgType.GetField("options", BindingFlags.NonPublic | BindingFlags.Instance);
                var cgOutput = cgType.GetField("output", BindingFlags.NonPublic | BindingFlags.Instance);

                if (cgOptions == null || cgOutput == null)
                {
                    return false;
                }

                initializeCodeGenerator = new Action<CodeGenerator, StringWriter, CodeGeneratorOptions>(
                    (generator, sw, options) =>
                    {
                        var output = new IndentedTextWriter(sw);
                        cgOptions.SetValue(generator, options);
                        cgOutput.SetValue(generator, output);
                    });
            }

            cgFieldGen = cgType.GetMethod("GenerateField", BindingFlags.NonPublic | BindingFlags.Instance);
            cgPropGen = cgType.GetMethod("GenerateProperty", BindingFlags.NonPublic | BindingFlags.Instance);
            cgMethGen = cgType.GetMethod("GenerateMethod", BindingFlags.NonPublic | BindingFlags.Instance);

            if (cgFieldGen == null || cgPropGen == null || cgMethGen == null)
            {
                return false;
            }

            return true;
        }

        public static string GenerateIndentedClassCode(CodeDomProvider provider, params CodeTypeMember[] members)
        {
            return GenerateIndentedClassCode(provider, (IEnumerable<CodeTypeMember>) members);
        }

        public static string GenerateIndentedClassCode(CodeDomProvider provider, IEnumerable<CodeTypeMember> members)
        {
            var options = new CodeGeneratorOptions();
            using (var sw = new StringWriter())
            {
                TemplatingEngine.GenerateCodeFromMembers(provider, options, sw, members);
                return TemplatingEngine.IndentSnippetText(provider, sw.ToString(), "        ");
            }
        }
    }
}