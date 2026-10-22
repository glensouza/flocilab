namespace FlociLab.Core;

/// <summary>
/// A difference between the emulator and the real service, recorded on the demo that observed it.
/// The detail is a sentence that already appears in that demo's source, so the
/// <c>/differences</c> page is an index of what the samples say rather than a second write-up.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ServiceFindingAttribute : Attribute
{
    public ServiceFindingAttribute(string title, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        this.Title = title;
        this.Detail = detail;
    }

    public string Title { get; }

    public string Detail { get; }
}
