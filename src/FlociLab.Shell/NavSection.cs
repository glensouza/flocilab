namespace FlociLab.Shell;

// A sidebar block a page-only RCL contributes under the fixed links: the comparison pages' links,
// registered by AddComparisonPages() itself so the links and the pages they point at cannot be
// wired one without the other.
internal sealed record NavSection(Type Component);
