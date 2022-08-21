using JetBrains.Annotations;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BuildTask = Microsoft.Build.Utilities.Task;

namespace IgnoresAccessChecksToGenerator.Tasks;

[PublicAPI]
public sealed class PublicizeInternals : BuildTask
{
	private static readonly char[] Semicolon = { ';' };

	private readonly string _sourceDir = Directory.GetCurrentDirectory();

	private readonly AssemblyResolver _resolver = new();

	[Required]
	public ITaskItem[] SourceReferences { get; set; } = Array.Empty<ITaskItem>();

	[Required]
	public string AssemblyNames { get; set; } = string.Empty;

	public string? ExcludeTypeNames { get; set; }

	public bool UseEmptyMethodBodies { get; set; } = true;

	[Output]
	public ITaskItem[]? TargetReferences { get; set; } = null;

	[Output]
	public ITaskItem[]? RemovedReferences { get; set; } = null;

	[Output]
	public ITaskItem[]? GeneratedCodeFiles { get; set; } = null;

	private string TargetPath =>
		Path.Combine(_sourceDir, "obj", "GeneratedPublicizedAssemblies");

	public override bool Execute()
	{
		if (!GetAssemblyNames(AssemblyNames, out var assemblyNames))
		{
			return true;
		}

		var targetPath = TargetPath;
		var targetDirectory = Directory.CreateDirectory(targetPath);

		if (!targetDirectory.Exists)
		{
			throw new DirectoryNotFoundException($"Failed to create directory '{targetPath}'");
		}

		GenerateAttributes(targetPath, assemblyNames);

		foreach (var assemblyPath in SourceReferences
							.Select(a => Path.GetDirectoryName(GetFullFilePath(a.ItemSpec))))
		{
			if (assemblyPath is not null)
			{
				_resolver.AddSearchDirectory(assemblyPath);
			}
		}

		var targetReferences = new List<ITaskItem>(SourceReferences.Length);
		var removedReferences = new List<ITaskItem>(SourceReferences.Length);

		foreach (var assembly in SourceReferences)
		{
			var assemblyPath = GetFullFilePath(assembly.ItemSpec);
			var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
			if (assemblyName is not null && assemblyNames.Contains(assemblyName))
			{
				var assemblyFileName = Path.GetFileName(assemblyPath)!;
				/*
				var assemblyExtension = Path.GetExtension(assemblyFileName) ?? "";
				var newExtension = $".public{assemblyExtension}";
				if (assemblyExtension.Length > 0)
				{
					assemblyFileName = assemblyFileName.Substring(
						0,
						assemblyFileName.Length - assemblyExtension.Length
					);
				}
				assemblyFileName += newExtension;
				*/


				// ReSharper disable once AssignNullToNotNullAttribute
				var targetAssemblyPath = Path.Combine(targetPath, assemblyFileName);

				var targetAssemblyFileInfo = new FileInfo(targetAssemblyPath);
				if (!targetAssemblyFileInfo.Exists || targetAssemblyFileInfo.Length == 0)
				{
					CreatePublicAssembly(assemblyPath, targetAssemblyPath);
					Log.LogMessageFromText($"Created publicized assembly at {targetAssemblyPath}", MessageImportance.Normal);
				}
				else
				{
					Log.LogMessageFromText($"Publicized assembly already exists at {targetAssemblyPath}", MessageImportance.Low);
				}

				var newTaskItem = new TaskItem(targetAssemblyPath, assembly.CloneCustomMetadata());
				assembly.CopyMetadataTo(newTaskItem);

				/*
				static void CopyMetadata(string name, ITaskItem from, ITaskItem to)
				{
					if (from.GetMetadata(name) is { } value)
					{
						to.SetMetadata(name, value);
					}
				}

				void DumpTaskItem(ITaskItem item, string name)
				{
					Log.LogMessage(MessageImportance.High, name);
					Log.LogMessage(MessageImportance.High, "----------------------");
					Log.LogMessage(MessageImportance.High, $"Type: {item.GetType()}");
					Log.LogMessage(MessageImportance.High, $"ItemSpec: {item.ItemSpec}");
					Log.LogMessage(MessageImportance.High, "Metadata");
					foreach (var key in item.MetadataNames)
					{
						var value = item.GetMetadata((string)key);
						Log.LogMessage(MessageImportance.High, $"\t{key} : {value}");
					}
					Log.LogMessage(MessageImportance.High, "----------------------");
					Log.LogMessage(MessageImportance.High, "");
				}

				DumpTaskItem(assembly, "Original");
				DumpTaskItem(newTaskItem, "Public");
				*/

				targetReferences.Add(newTaskItem);
				removedReferences.Add(assembly);
			}
		}

		TargetReferences = targetReferences.ToArray();
		RemovedReferences = removedReferences.ToArray();

		return true;
	}

	private bool GetAssemblyNames(string assemblyNamesProperty, [NotNullWhen(true)] out HashSet<string>? assemblyNames)
	{
		var assemblies = new HashSet<string>(
			AssemblyNames.Split(Semicolon, StringSplitOptions.RemoveEmptyEntries),
			PathUtility.DirectoryComparer
		);

		if (assemblies.Count == 0)
		{
			assemblyNames = null;
			return false;
		}

		assemblyNames = assemblies;
		return true;
	}

	private static readonly string[] AttributeStringLines =
	{
		"namespace System.Runtime.CompilerServices;",
		"",
		"[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]",
		"internal sealed class IgnoresAccessChecksToAttribute : Attribute",
		"{",
		"\tpublic IgnoresAccessChecksToAttribute(string assemblyName)",
		"\t{",
		"\t}",
		"}"
	};

	private void GenerateAttributes(string path, ICollection<string> assemblyNames)
	{
		var filePath = Path.Combine(path, "IgnoresAccessChecksTo.cs");
		{
			using var file = File.CreateText(filePath);

			foreach (var assemblyName in assemblyNames)
			{
				file.WriteLine($@"[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo(""{assemblyName}"")]");
			}

			file.WriteLine();

			foreach (var line in AttributeStringLines)
			{
				file.WriteLine(line);
			}

			file.WriteLine();
		}

		GeneratedCodeFiles = new ITaskItem[] { new TaskItem(filePath) };

		Log.LogMessageFromText("Generated IgnoresAccessChecksTo attributes", MessageImportance.Low);
	}

	private void CreatePublicAssembly(string source, string target)
	{
		var types = ExcludeTypeNames is null ? Array.Empty<string>() : ExcludeTypeNames.Split(Semicolon);

		var assembly = AssemblyDefinition.ReadAssembly(
			source,
			new()
			{
				AssemblyResolver = _resolver,
				InMemory = true,
				ReadSymbols = true
			}
		);

		void PublicizeType(TypeDefinition type)
		{
			if (types.Length != 0 && !types.Contains(type.FullName))
			{
				return;
			}

			if (!type.IsNested && type.IsNotPublic)
			{
				type.IsPublic = true;
			}
			else if (type.IsNestedAssembly ||
							type.IsNestedFamilyOrAssembly ||
							type.IsNestedFamilyAndAssembly)
			{
				type.IsNestedPublic = true;
			}

			foreach (var field in type.Fields)
			{
				if (field.IsAssembly ||
						field.IsFamilyOrAssembly ||
						field.IsFamilyAndAssembly)
				{
					field.IsPublic = true;
				}
			}

			foreach (var method in type.Methods)
			{
				if (UseEmptyMethodBodies && method.HasBody)
				{
					var emptyBody = new Mono.Cecil.Cil.MethodBody(method);
					emptyBody.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldnull));
					emptyBody.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Throw));
					method.Body = emptyBody;
				}

				if (method.IsAssembly ||
						method.IsFamilyOrAssembly ||
						method.IsFamilyAndAssembly)
				{
					method.IsPublic = true;
				}
			}
		}

		Parallel.ForEach(
			assembly.Modules.SelectMany(module => module.GetTypes()),
			PublicizeType
		);

		assembly.Write(target);
	}

	private string GetFullFilePath(string path)
	{
		if (!Path.IsPathRooted(path))
		{
			path = Path.Combine(_sourceDir, path);
		}

		path = Path.GetFullPath(path);
		return path;
	}
}