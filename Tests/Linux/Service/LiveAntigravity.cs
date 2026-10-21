using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PersonalTools;

internal static class LiveAntigravity
{
    public static async Task RunAsync()
    {
        string temporary=Path.Combine(Path.GetTempPath(),"personal-tools-ai-live-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);File.SetUnixFileMode(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var checks=new List<object>();
        void Check(bool passed,string name,object? detail=null){checks.Add(new{name,passed,detail});Console.WriteLine((passed?"PASS ":"FAIL ")+name);if(!passed)throw new Exception(name);}
        try
        {
            using var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();int port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
            var settings=new BridgeSettings(BridgeSettings.DefaultCli,port,ClipboardSearch.Model,180);
            var models=await AntigravityBridge.GetModelsAsync(settings,default);Check(models.Contains(ClipboardSearch.Model),"logged-in CLI model discovery",new{count=models.Length,model=ClipboardSearch.Model});
            await using var bridge=new AntigravityBridge();await bridge.StartAsync(settings);
            using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(190)};client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",bridge.ApiKey);
            var request=JsonSerializer.Serialize(new{model=ClipboardSearch.Model,messages=new[]{new{role="user",content="Reply with only the word OK. Do not use tools."}},stream=true});
            using var response=await client.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions",new StringContent(request,Encoding.UTF8,"application/json"));
            string sse=await response.Content.ReadAsStringAsync();Check(response.IsSuccessStatusCode&&sse.Contains("[DONE]")&&sse.Contains("OK"),"real authenticated HTTP/SSE bridge response");
            var store=new ClipboardStore(new(){ArchivePath=Path.Combine(temporary,"archive"),AiSearchEnabled=true,AutoTagImages=true});await store.LoadAsync();
            var sample=await store.SaveAsync(new("Text","io.griplabs.PersonalTools.Test",Text:"공개 테스트 데이터: 올리브 디자인의 기준 간격은 24 픽셀입니다. 확인 코드 PT-4829."));
            await store.SaveAsync(new("Text","io.griplabs.PersonalTools.Test",Text:"공개 테스트 데이터: 바다 사진에는 파란색이 사용됩니다."));
            var search=new ClipboardSearch(bridge,store,()=>settings);
            var found=await search.SearchClipboardAsync("PT-4829 기록에 적힌 기준 간격은 몇 픽셀인가요?",new[]{sample!.Id},default);
            Check(found.ItemIds.SequenceEqual(new[]{sample.Id})&&found.Answer.Contains("24"),"actual scoped archive retrieval",new{resultCount=found.ItemIds.Length,limitations=found.Limitations});
            string fixture=Path.GetFullPath("Sources/Linux/App/assets/mountain.png");
            string image=Path.Combine(temporary,"public-mountain-fixture.png");File.Copy(fixture,image);
            var tags=await ClipboardImageTagger.AnalyzeAsync(settings,image,default);Check(tags.Tags.Length>0&&!string.IsNullOrWhiteSpace(tags.Description),"actual image view and structured tagging",new{tagCount=tags.Tags.Length,model=tags.Model});
            await bridge.StopAsync();Check(!bridge.IsRunning,"real bridge shutdown");
        }
        finally
        {
            string report=Path.GetFullPath("build/linux/antigravity-live.json");Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report,JsonSerializer.Serialize(new{observedAt=DateTimeOffset.UtcNow,scope="Synthetic public sample archive and image only; no personal clipboard data",checks},new JsonSerializerOptions{WriteIndented=true}));
            Directory.Delete(temporary,true);
        }
    }
}
