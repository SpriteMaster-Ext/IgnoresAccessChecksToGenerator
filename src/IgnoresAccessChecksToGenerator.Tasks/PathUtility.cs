using System;
using System.Runtime.InteropServices;

namespace IgnoresAccessChecksToGenerator.Tasks;

internal static class PathUtility
{
	// This isn't actually correct - we should check on a path-by-path basis as Windows and MacOS can specify
	// for case-sensitivity on a directory- or partition-basis.
	private static StringComparer DirectoryComparerInternal =>
		false switch
		{
			_ when RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX) =>
				StringComparer.OrdinalIgnoreCase,
			_ when RuntimeInformation.IsOSPlatform(OSPlatform.Linux) =>
				StringComparer.Ordinal,
			_ when Environment.OSVersion.Platform == PlatformID.Unix =>
				StringComparer.OrdinalIgnoreCase,
			_ =>
				StringComparer.OrdinalIgnoreCase
		};

	internal static readonly StringComparer DirectoryComparer = DirectoryComparerInternal;
}
