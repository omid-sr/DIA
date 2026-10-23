namespace Api.DIA.Hosting;

public sealed class SchemaReady
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    public Task WaitAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    public void SetReady() => _ready.TrySetResult();
}
