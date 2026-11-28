using System.Text.Json;
using FileMind.Core;
using Xunit;

namespace FileMind.Core.Tests;

public class FileQueryTests
{
    [Fact] public void AppliesWhatTheEngineUnderstood()
    {
        var u = JsonSerializer.Deserialize<Understood>("""
            {"text":"a beach","filters":{"after":100.0,"before":200.0,"min_pixels":8294400,"min_duration":10,"aspect":"portrait"},
             "kinds":["video"],"color":"blue","source":null,"favourite":false,"chips":[{"key":"date","label":"Date","value":"1 Jan 1970"}]}
            """)!;
        var q = new FileQuery();
        var left = q.Apply(u);
        Assert.Equal("a beach", left);
        Assert.Equal("video", q.Kind);
        Assert.Equal(100, q.After);
        Assert.Equal(200, q.Before);
        Assert.Equal(8294400, q.MinPixels);
        Assert.Equal(10, q.MinDuration);
        Assert.Equal("portrait", q.Aspect);
        Assert.Equal("blue", q.Color);
        Assert.Equal("Date: 1 Jan 1970", u.Summary);
    }

    [Fact] public void SearchBodyCarriesTheClock()
    {
        var body = new FileQuery().ToSearchBody(["beach"], 10, 0);
        Assert.True(body.ContainsKey("now"));
        Assert.True(body.ContainsKey("utc_offset_min"));
        Assert.True(body.ContainsKey("day_first"));
    }
}
