namespace StarDexs.Sync.Core;

/// <summary>Bound responses while streaming, including chunked bodies with no declared length.</summary>
internal static class HttpBody
{
    public static async Task<byte[]> ReadAsync(HttpResponseMessage response, int limit, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        cancel = deadline.Token;
        if (response.Content.Headers.ContentLength > limit) throw new HttpRequestException("The StarDad response was larger than expected.");
        await using var source = await response.Content.ReadAsStreamAsync(cancel);
        using var target = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancel)) != 0)
        {
            if (target.Length + read > limit) throw new HttpRequestException("The StarDad response was larger than expected.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancel);
        }
        return target.ToArray();
    }
}
