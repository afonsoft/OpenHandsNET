using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Conversations;

/// <summary>
/// Start-task store + in-process queue. The <see cref="ConversationStartWorker"/>
/// drains the channel; <see cref="WatchAsync"/> polls the row for SSE clients.
/// </summary>
public sealed class StartTaskService : IStartTaskService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Channel<Guid> _queue =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public StartTaskService(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <summary>Queue drained by <see cref="ConversationStartWorker"/>.</summary>
    internal ChannelReader<Guid> Queue => _queue.Reader;

    /// <inheritdoc />
    public ValueTask EnqueueAsync(Guid taskId, CancellationToken ct) =>
        _queue.Writer.WriteAsync(taskId, ct);

    /// <inheritdoc />
    public async Task<AppConversationStartTask?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.StartTasks.FindAsync([id], ct))?.ToDomain();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppConversationStartTask>> GetBatchAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wanted = ids.Take(100).ToHashSet();
        var rows = await db.StartTasks.Where(r => wanted.Contains(r.Id)).ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);
        return ids.Where(byId.ContainsKey).Select(id => byId[id].ToDomain()).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppConversationStartTask>> SearchAsync(
        StartTaskStatus? status, string? pageId, int limit, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var q = db.StartTasks.AsQueryable();
        if (status is not null)
        {
            var s = status.Value.ToString();
            q = q.Where(r => r.Status == s);
        }

        if (AppConversationService.TryDecodeCursor(pageId, out var ticks, out var id))
        {
            q = q.Where(r => r.CreatedAtTicks < ticks
                || (r.CreatedAtTicks == ticks
                    && string.Compare(r.Id.ToString(), id) < 0));
        }

        return (await q.OrderByDescending(r => r.CreatedAtTicks)
                .ThenByDescending(r => r.Id)
                .Take(Math.Clamp(limit, 1, 100))
                .ToListAsync(ct))
            .Select(r => r.ToDomain()).ToList();
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(StartTaskStatus? status, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var q = db.StartTasks.AsQueryable();
        if (status is not null)
        {
            var s = status.Value.ToString();
            q = q.Where(r => r.Status == s);
        }

        return await q.LongCountAsync(ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AppConversationStartTask> WatchAsync(
        Guid taskId, [EnumeratorCancellation] CancellationToken ct)
    {
        // Poll a 500 ms — suficiente p/ SSE de start-task (duração de segundos).
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            var task = await GetAsync(taskId, ct);
            if (task is null)
            {
                yield break;
            }

            if (task.Status.ToString() != last)
            {
                last = task.Status.ToString();
                yield return task;
            }

            if (task.Status is StartTaskStatus.Ready or StartTaskStatus.Error)
            {
                yield break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }
}
