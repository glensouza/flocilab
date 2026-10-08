using System.Reflection;

namespace FlociLab.Shell;

// What differs between hosts once the chrome is shared: the name in the sidebar, and the host's own
// assembly, which owns Home and names the host's scoped-CSS bundle.
internal sealed record ShellOptions(Assembly HostAssembly, string Title, string Subtitle);
