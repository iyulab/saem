using System.Text;
using Microsoft.Extensions.Logging;
using Saem.Connectors.MorphDb;
using Saem.Connectors.Postgres;
using Saem.Ontology;
using Saem.Query;

namespace Saem.Materialization.Cli;

/// <summary>What a job did: whether it held, and the report it filed as JSON when it files one.</summary>
internal sealed record JobOutcome(bool Succeeded, string? Report = null);

/// <summary>What a request adds to the kind of job it names; only some kinds take any.</summary>
/// <param name="Identity">The declared identity a fit is for.</param>
internal sealed record JobArguments(string? Identity = null);

/// <summary>The work of one kind of job, writing the lines the one-shot command prints to <paramref name="output"/>.</summary>
internal delegate Task<JobOutcome> JobWork(JobArguments arguments, TextWriter output, CancellationToken cancellationToken);

/// <summary>One job as a caller sees it.</summary>
internal sealed record JobView(
    string Id,
    string Kind,
    string RequestedBy,
    string? Identity,
    string State,
    int? ExitCode,
    string Output,
    bool OutputTruncated,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Report);

/// <summary>
/// The jobs an executor has run, and the one it is running. An executor holds one group's secrets and writes one
/// group's store, so it does one job at a time: asking for another while one runs is refused, not queued, and the
/// refusal names the job in the way. Only the last few are kept, in memory — what a job did is in the group's store.
/// </summary>
internal sealed partial class JobStore(TimeProvider time, ILogger logger, int keep = 20, int outputLimit = 256 * 1024)
{
    private readonly object _gate = new();
    private readonly List<Job> _jobs = [];

    /// <summary>
    /// Starts <paramref name="work"/> unless a job is running, in which case that job is returned instead.
    /// </summary>
    /// <returns><see langword="true"/> when the job was started.</returns>
    public bool TryStart(string kind, string requestedBy, JobArguments arguments, JobWork work, CancellationToken stopping, out JobView job)
    {
        Job started;
        lock (_gate)
        {
            if (_jobs.Find(j => j.State == "running") is { } running)
            {
                job = running.View();
                return false;
            }

            started = new Job(Guid.NewGuid().ToString("N"), kind, requestedBy, arguments, time.GetUtcNow(), outputLimit, CancellationTokenSource.CreateLinkedTokenSource(stopping));
            _jobs.Add(started);
            while (_jobs.Count > keep)
            {
                _jobs.RemoveAt(0);
            }

            job = started.View();
        }

        _ = Task.Run(() => RunAsync(started, work));
        return true;
    }

    public JobView? Get(string id)
    {
        lock (_gate)
        {
            return _jobs.Find(j => j.Id == id)?.View();
        }
    }

    /// <summary>The kept jobs, the most recent first.</summary>
    public IReadOnlyList<JobView> Recent()
    {
        lock (_gate)
        {
            return [.. _jobs.AsEnumerable().Reverse().Select(j => j.View())];
        }
    }

    /// <summary>Asks a running job to stop.</summary>
    /// <returns><see langword="null"/> when there is no such job; whether the job was still running otherwise.</returns>
    public bool? Cancel(string id)
    {
        Job? job;
        lock (_gate)
        {
            job = _jobs.Find(j => j.Id == id);
            if (job is null)
            {
                return null;
            }

            if (job.State != "running")
            {
                return false;
            }
        }

        job.Cancellation.Cancel();
        return true;
    }

    /// <summary>Waits up to <paramref name="patience"/> for the running job, if any, to finish — for an executor that is being stopped.</summary>
    public async Task DrainAsync(TimeSpan patience)
    {
        Job? running;
        lock (_gate)
        {
            running = _jobs.Find(j => j.State == "running");
        }

        if (running is not null)
        {
            await Task.WhenAny(running.Done.Task, Task.Delay(patience, time)).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Id} ({Kind}) failed unexpectedly.")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, string id, string kind);

    private async Task RunAsync(Job job, JobWork work)
    {
        var state = "failed";
        var exitCode = 1;
        string? report = null;
        try
        {
            var outcome = await work(job.Arguments, job.Writer, job.Cancellation.Token).ConfigureAwait(false);
            (state, exitCode, report) = outcome.Succeeded ? ("succeeded", 0, outcome.Report) : ("failed", 1, outcome.Report);
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            (state, exitCode) = ("cancelled", 130);
            job.Writer.WriteLine("Cancelled.");
        }
        catch (MaterializeUsageException e)
        {
            (state, exitCode) = ("failed", 2);
            job.Writer.WriteLine(e.Message);
        }
        catch (Exception e) when (e is OntologyDeclarationException or NoSourceLoginException or MorphDbSourceException or InsecureSourceRoleException
                                  or System.Data.Common.DbException or HttpRequestException or IOException or InvalidOperationException
                                  or EdgeStoreException or IdentityStoreException)
        {
            job.Writer.WriteLine(e.Message);
        }
        catch (Exception e)
        {
            // The caller is told it failed, not what was inside: the executor's log has the rest.
            LogUnexpected(logger, e, job.Id, job.Kind);
            job.Writer.WriteLine($"The job failed unexpectedly ({e.GetType().Name}); the executor's log has the detail.");
        }

        lock (_gate)
        {
            job.Finish(state, exitCode, report, time.GetUtcNow());
        }

        job.Cancellation.Dispose();
        job.Done.SetResult();
    }

    private sealed class Job(string id, string kind, string requestedBy, JobArguments arguments, DateTimeOffset startedAt, int outputLimit, CancellationTokenSource cancellation)
    {
        private readonly StringBuilder _output = new();
        private bool _truncated;
        private int? _exitCode;
        private DateTimeOffset? _finishedAt;
        private string? _report;
        private TextWriter? _writer;

        public string Id { get; } = id;

        public string Kind { get; } = kind;

        public JobArguments Arguments { get; } = arguments;

        public string State { get; private set; } = "running";

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TextWriter Writer => _writer ??= new LimitedWriter(this);

        public void Finish(string state, int exitCode, string? report, DateTimeOffset at)
        {
            lock (_output)
            {
                State = state;
                _exitCode = exitCode;
                _report = report;
                _finishedAt = at;
            }
        }

        public JobView View()
        {
            lock (_output)
            {
                return new JobView(Id, Kind, requestedBy, Arguments.Identity, State, _exitCode, _output.ToString(), _truncated, startedAt, _finishedAt, _report);
            }
        }

        private void Append(ReadOnlySpan<char> text)
        {
            lock (_output)
            {
                var room = outputLimit - _output.Length;
                if (text.Length > room)
                {
                    _truncated = true;
                    text = text[..Math.Max(room, 0)];
                }

                _output.Append(text);
            }
        }

        private sealed class LimitedWriter(Job job) : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value) => job.Append([value]);

            public override void Write(string? value) => job.Append(value);

            public override void Write(char[] buffer, int index, int count) => job.Append(buffer.AsSpan(index, count));
        }
    }
}
