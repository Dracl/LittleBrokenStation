using System.Runtime.CompilerServices;

// LBS fork: the download runtime was moved out of DownKyi.Desktop. These assemblies already
// had access to those internals before the move (see src/DownKyi.Desktop/Properties/AssemblyInfo.cs),
// so the same access is granted to the Worker assembly to keep the existing consumers compiling.
[assembly: InternalsVisibleTo("DownKyi.Desktop")]
[assembly: InternalsVisibleTo("DownKyi.Tests")]
[assembly: InternalsVisibleTo("DownKyi.Desktop.Tests")]
[assembly: InternalsVisibleTo("DownKyi.SystemBenchmarks")]
[assembly: InternalsVisibleTo("DownKyi.Windows.Tests")]
