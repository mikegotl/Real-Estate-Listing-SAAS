using System.Diagnostics;

namespace ListingStudio.Application.Administration;

public sealed class AiUsageHttpMessageHandler(
    IAiUsageRecorder recorder,
    string provider,
    string model,
    string operation,
    decimal estimatedCostPerCallUsd,
    Func<HttpRequestMessage, bool>? shouldRecord = null,
    Func<HttpRequestMessage, HttpResponseMessage?, CancellationToken, ValueTask<decimal>>? estimateCost = null)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var recordUsage = shouldRecord?.Invoke(request) ?? true;
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage? response = null;
        string? failureCategory = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                failureCategory = $"http_{(int)response.StatusCode / 100}xx";
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failureCategory = "timeout";
            throw;
        }
        catch (HttpRequestException)
        {
            failureCategory = "network";
            throw;
        }
        catch
        {
            failureCategory = "unexpected";
            throw;
        }
        finally
        {
            if (recordUsage)
            {
                var estimatedCostUsd = estimateCost is null
                    ? estimatedCostPerCallUsd
                    : await estimateCost(request, response, CancellationToken.None);
                await recorder.RecordAsync(
                    new AiCallObservation(
                        provider,
                        model,
                        operation,
                        response?.IsSuccessStatusCode == true,
                        Stopwatch.GetElapsedTime(started),
                        estimatedCostUsd,
                        failureCategory),
                    CancellationToken.None);
            }
        }
    }
}
