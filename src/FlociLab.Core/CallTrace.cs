namespace FlociLab.Core;

/// <summary>
/// A capability records the call it is about to make, and what came back, so a comparison cell
/// can show both. The comparison page references no cloud SDK, so it cannot see the call itself.
///
/// <para>
/// Ambient and optional. A capability method calls <see cref="Request"/> and <see cref="Response"/>
/// and they do nothing unless a comparison run opened a <see cref="Buffer"/> with <see cref="Begin"/>.
/// The integration tests call the same methods and never open one.
/// </para>
/// </summary>
public static class CallTrace
{
    private static readonly AsyncLocal<Buffer?> Ambient = new AsyncLocal<Buffer?>();

    public static IDisposable Begin(Buffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return new Scope(buffer);
    }

    /// <summary>The last request wins. A method that lists and then deletes records the delete.</summary>
    public static void Request(string request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Ambient.Value is { } buffer)
        {
            buffer.Request = request;
        }
    }

    public static void Response(string response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (Ambient.Value is { } buffer)
        {
            buffer.Response = response;
        }
    }

    public sealed class Buffer
    {
        public string? Request { get; set; }

        public string? Response { get; set; }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Buffer? previous;

        public Scope(Buffer buffer)
        {
            this.previous = Ambient.Value;
            Ambient.Value = buffer;
        }

        public void Dispose() => Ambient.Value = this.previous;
    }
}
