using FlociLab.Core;

namespace FlociLab.Comparison;

/// <summary>
/// Runs one comparison operation inside a <see cref="CallTrace"/>, so the capability can record
/// the request and the response while this page still classifies the exception itself.
/// <see cref="OperationCanceledException"/> is left to escape: a cancelled column is not a result.
/// </summary>
internal static class ComparisonTrace
{
    public static async Task<TraceOutcome> RunAsync(Func<Task<string>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        CallTrace.Buffer trace = new CallTrace.Buffer();

        using (CallTrace.Begin(trace))
        {
            try
            {
                string detail = await operation().ConfigureAwait(false);
                return new TraceOutcome(detail, null, trace.Request, trace.Response);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new TraceOutcome(ex.Message, ex, trace.Request, trace.Response);
            }
        }
    }

    internal sealed record TraceOutcome(string Detail, Exception? Error, string? Request, string? Response);
}
