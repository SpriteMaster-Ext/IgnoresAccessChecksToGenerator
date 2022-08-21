using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;

namespace IgnoresAccessChecksToGenerator.Tasks;

internal sealed class AssemblyResolver : IAssemblyResolver
{
	private readonly HashSet<string> _directories = new(PathUtility.DirectoryComparer);

	public void AddSearchDirectory(string directory)
	{
		_directories.Add(directory);
	}

	public AssemblyDefinition Resolve(AssemblyNameReference name)
	{
		return Resolve(name, new());
	}

	public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
	{
		if (SearchDirectory(name, _directories, parameters) is {} assembly)
		{
			return assembly;
		}

		throw new AssemblyResolutionException(name);
	}

	public void Dispose()
	{
	}

	private static readonly string[] WindowsRuntimeExtensions = { ".winmd", ".dll" };
	private static readonly string[] AssemblyExtensions = { ".exe", ".dll" };

	private AssemblyDefinition? SearchDirectory(AssemblyNameReference name, HashSet<string> directories, ReaderParameters parameters)
	{
		var extensions = name.IsWindowsRuntime ? WindowsRuntimeExtensions : AssemblyExtensions;
		foreach (var directory in directories)
		{
			string assemblyName = name.Name;

			foreach (var extension in extensions)
			{
				var file = Path.Combine(directory, assemblyName + extension);
				if (!File.Exists(file))
				{
					continue;
				}

				try
				{
					return GetAssembly(file, parameters);
				}
				catch (BadImageFormatException)
				{
					// swallow exception
				}
			}
		}

		return null;
	}

	private AssemblyDefinition GetAssembly(string file, ReaderParameters parameters)
	{
		parameters.AssemblyResolver ??= this;

		return ModuleDefinition.ReadModule(file, parameters).Assembly;
	}
}
