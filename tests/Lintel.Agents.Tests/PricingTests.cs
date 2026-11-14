// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json.Nodes;
using Lintel.Agents.Logs;
using Lintel.Agents.Pricing;
using Lintel.Core.Agents;

namespace Lintel.Agents.Tests;

public sealed class PricingTests
{
    private static readonly PriceList List = PriceSource.Embedded();
    private static readonly AgentPricing Pricing = new(List);
    private const double Tolerance = 1e-9;

    private static Billable B(long input = 0, long cw = 0, long cr = 0, long output = 0, long longWrite = 0, bool fast = false,
                              bool domestic = false, int web = 0, bool aggregate = false) =>
        new(new AgentTokens(input, cw, cr, output, 0), longWrite, fast, domestic, web, aggregate);

    [Fact]
    public void EmbeddedListDecodes()
    {
        Assert.NotEqual(PriceList.Empty, List);
        Assert.Equal(new DateOnly(2026, 11, 13), List.Updated);
        Assert.True(List.Claude.Count > 10 && List.Codex.Count > 10);
        Assert.Equal(0.01, List.WebSearch);
        Assert.Equal(1.1, List.UsOnlyMultiplier);
    }

    [Theory]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    [InlineData("claude-sonnet-4-5-20250929", "claude-sonnet-4-5")]            // dated snapshot is the same model
    [InlineData("Claude-Opus-5.5", "claude-opus-5-5")]                         // Copilot's dotted point release, any case
    [InlineData("anthropic/claude-opus-5-5", "claude-opus-5-5")]
    [InlineData("us.anthropic.claude-opus-5-5-v1:0", "claude-opus-5-5")]       // Bedrock
    [InlineData("claude-opus-5-5@20260301", "claude-opus-5-5")]                // Vertex
    [InlineData("claude-opus-5-5[1m]", "claude-opus-5-5")]                     // context tag
    [InlineData("gpt-6.1-sol", "gpt-6.1-sol")]
    [InlineData("openai/gpt-6.1-sol-2026-10-01", "gpt-6.1-sol")]
    public void PriceFindsTheFamilyAtAWordBoundary(string model, string family) =>
        Assert.Equal(family, Pricing.Price(model)?.Id);

    [Theory]
    [InlineData("claude-opus-5-6")]        // a point release the list does not name never inherits the version it extends
    [InlineData("gpt-6.1-sol-mini")]       // sibling guard: a smaller variant has its own price
    [InlineData("gpt-6.1-sol-pro")]
    [InlineData("gpt-6.1-solar")]          // not a word boundary
    [InlineData("totally-unknown-model")]
    [InlineData("")]
    public void UnknownOrSiblingModelsAreUnpriced(string model) => Assert.Null(Pricing.Price(model));

    [Fact]
    public void LongestFamilyWins()
    {
        Assert.Equal("claude-opus-5-5-fast", Pricing.Price("claude-opus-5-5-fast")?.Id);
        Assert.Equal("claude-opus-5-5", Pricing.Price("claude-opus-5-5")?.Id);
        Assert.Equal("claude-opus-5", Pricing.Price("claude-opus-5")?.Id);
    }

    [Fact]
    public void CostFormulaCoversEveryTokenCategoryAndSavings()
    {
        // claude-opus-5-5: in 4, out 20, cacheRead 0.2, cacheWrite 5, 1-hour write 8 (per million).
        var (cost, savings) = Pricing.Cost(B(input: 1_000_000, cw: 1_000_000, cr: 1_000_000, output: 1_000_000, longWrite: 400_000), "claude-opus-5-5");
        Assert.Equal(4 + (600_000 * 5 + 400_000 * 8) / 1e6 + 0.2 + 20, cost!.Value, Tolerance);
        Assert.Equal(1_000_000 * (4 - 0.2) / 1e6, savings, Tolerance);
    }

    [Fact]
    public void OneHourWriteIsCappedAtTheCacheWriteCount()
    {
        var (a, _) = Pricing.Cost(B(cw: 1_000_000, longWrite: 5_000_000), "claude-opus-5-5");
        Assert.Equal(8, a!.Value, Tolerance);
    }

    [Fact]
    public void FastUsOnlyAndWebSearchMultiply()
    {
        var plain = Pricing.Cost(B(input: 1_000_000, output: 1_000_000), "claude-opus-5-5").Cost!.Value;
        Assert.Equal(24, plain, Tolerance);
        Assert.Equal(plain * 2, Pricing.Cost(B(input: 1_000_000, output: 1_000_000, fast: true), "claude-opus-5-5").Cost!.Value, Tolerance);
        Assert.Equal(plain * 1.1, Pricing.Cost(B(input: 1_000_000, output: 1_000_000, domestic: true), "claude-opus-5-5").Cost!.Value, Tolerance);
        Assert.Equal(plain * 2 * 1.1, Pricing.Cost(B(input: 1_000_000, output: 1_000_000, fast: true, domestic: true), "claude-opus-5-5").Cost!.Value, Tolerance);
        Assert.Equal(0.03, Pricing.Cost(B(web: 3), "claude-opus-5-5").Cost!.Value, Tolerance);   // web searches are a flat fee
    }

    [Fact]
    public void LongContextPremiumAppliesToTheWholeRequestUnlessAggregate()
    {
        // claude-sonnet-4-5: in 3, out 15; above 200k prompt tokens input rates x2 and output x1.5.
        var big = Pricing.Cost(B(input: 300_000, output: 100_000), "claude-sonnet-4-5").Cost!.Value;
        Assert.Equal(300_000 * 3 * 2 / 1e6 + 100_000 * 15 * 1.5 / 1e6, big, Tolerance);
        var below = Pricing.Cost(B(input: 100_000, output: 100_000), "claude-sonnet-4-5").Cost!.Value;
        Assert.Equal(100_000 * 3 / 1e6 + 100_000 * 15 / 1e6, below, Tolerance);
        // Session totals do not reveal prompt sizes: priced at base rates.
        var aggregate = Pricing.Cost(B(input: 300_000, output: 100_000, aggregate: true), "claude-sonnet-4-5").Cost!.Value;
        Assert.Equal(300_000 * 3 / 1e6 + 100_000 * 15 / 1e6, aggregate, Tolerance);
    }

    [Fact]
    public void CodexFastTierAndCachedInput()
    {
        // gpt-6.1-sol: in 2, out 10, cacheRead 0.1, fast x2. Codex log tokens: input 1000 incl. 600 cached -> Input 400.
        var b = new Billable(new AgentTokens(400, 0, 600, 70, 20));
        var (cost, savings) = Pricing.Cost(b, "gpt-6.1-sol");
        Assert.Equal((400 * 2 + 600 * 0.1 + 70 * 10) / 1e6, cost!.Value, Tolerance);
        Assert.Equal(600 * (2 - 0.1) / 1e6, savings, Tolerance);
        Assert.Equal(cost.Value * 2, Pricing.Cost(b with { Fast = true }, "gpt-6.1-sol").Cost!.Value, Tolerance);
    }

    [Fact]
    public void UnknownModelCostsNullButActivityOnlyRecordsCostNothing()
    {
        Assert.Null(Pricing.Cost(B(input: 10), "mystery-model").Cost);
        Assert.Equal((0.0, 0.0), Pricing.Cost(B(aggregate: true), "mystery-model"));
    }

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-sonnet-4-5-20250929", "Sonnet 4.5")]
    [InlineData("claude-opus-5-5:thinking", "Opus 5.5")]
    [InlineData("claude-3-5-haiku", "Haiku 3.5")]
    [InlineData("claude-haiku-5-2-fast", "Haiku 5.2 Fast")]
    [InlineData("gpt-6-astra", "GPT-6 Astra")]
    [InlineData("gpt-6.1-sol", "GPT-6.1 Sol")]
    [InlineData("gpt-5-2026-10-01", "GPT-5")]
    [InlineData("gpt-latest", "GPT")]
    [InlineData("some-other-model", "some-other-model")]
    [InlineData("", "")]
    public void DisplayNames(string model, string expected) => Assert.Equal(expected, AgentPricing.DisplayName(model));

    [Fact]
    public void Plans()
    {
        Assert.Equal(new AgentPlan("Ultra", 200), AgentPlans.Claude("max", "default_claude_max_20x", List));
        Assert.Equal(new AgentPlan("Max", 100), AgentPlans.Claude("max", null, List));
        Assert.Equal(new AgentPlan("Pro", 20), AgentPlans.Claude("pro", "", List));
        Assert.Equal(new AgentPlan("Enterprise", null), AgentPlans.Claude("enterprise", null, List));
        Assert.Equal("Mystery Plan", AgentPlans.Claude("claude_mystery_plan", null, List)?.Name);
        Assert.Null(AgentPlans.Claude(null, null, List));
        Assert.Equal(new AgentPlan("Plus", 20), AgentPlans.Codex("plus", List));
        Assert.Equal(new AgentPlan("Pro", null), AgentPlans.Codex(" PRO ", List));
        Assert.Equal(new AgentPlan("Newtier", null), AgentPlans.Codex("newtier", List));
        Assert.Null(AgentPlans.Codex("", List));
    }
}

public sealed class PriceListValidationTests
{
    private static string EmbeddedText()
    {
        using var s = typeof(AgentsData).Assembly.GetManifestResourceStream("agent-prices.json")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static byte[] Mutate(Action<JsonNode> change)
    {
        var node = JsonNode.Parse(EmbeddedText())!;
        change(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private static PriceList? Decode(Action<JsonNode> change) => PriceList.Decode(Mutate(change));

    [Fact]
    public void UnchangedListDecodes() => Assert.NotNull(Decode(_ => { }));

    [Fact]
    public void WrongSchemaIsRefused()
    {
        Assert.Null(Decode(n => n["schema"] = 2));
        Assert.Null(Decode(n => n["schema"] = "1"));
        Assert.Null(Decode(n => n.AsObject().Remove("schema")));
    }

    [Fact]
    public void OversizeIsRefused()
    {
        var padded = Mutate(n => n["pad"] = new string('x', PriceList.MaximumSize));
        Assert.True(padded.Length > PriceList.MaximumSize);
        Assert.Null(PriceList.Decode(padded));
    }

    [Fact]
    public void NotJsonOrNotAnObjectIsRefused()
    {
        Assert.Null(PriceList.Decode("nope"u8));
        Assert.Null(PriceList.Decode("[]"u8));
        Assert.Null(PriceList.Decode(""u8));
    }

    [Fact]
    public void DuplicateIdsAreRefused() =>
        Assert.Null(Decode(n => n["claude"]!["models"]!.AsArray().Add(JsonNode.Parse(n["claude"]!["models"]![0]!.ToJsonString())!)));

    [Fact]
    public void BadFieldsAreRefusedAsAWhole()
    {
        Assert.Null(Decode(n => n["claude"]!["models"]![0]!["input"] = -1));
        Assert.Null(Decode(n => n["claude"]!["models"]![0]!["input"] = 5000));
        Assert.Null(Decode(n => n["claude"]!["models"]![0]!["input"] = "4"));
        Assert.Null(Decode(n => n["claude"]!["models"]![0]!["input"] = true));
        Assert.Null(Decode(n => n["claude"]!["models"]![0]!["fastMultiplier"] = 0.5));
        Assert.Null(Decode(n => n["claude"]!["models"]![0]!["id"] = "Claude-Upper"));
        Assert.Null(Decode(n => n["claude"]!["models"]![0]!["id"] = "claude bad id"));
        Assert.Null(Decode(n => n["claude"]!["models"]![0]!["longContext"] = JsonNode.Parse("""{"above":5,"inputMultiplier":2,"outputMultiplier":2}""")));
        Assert.Null(Decode(n => n["claude"]!["webSearch"] = 2));
        Assert.Null(Decode(n => n["claude"]!["usOnlyMultiplier"] = 0.5));
    }

    [Fact]
    public void ProviderPrefixRulesAreEnforced()
    {
        Assert.Null(Decode(n => n["claude"]!["models"]![0]!["id"] = "gpt-9"));                    // claude list needs claude- ids
        Assert.Null(Decode(n => n["codex"]!["models"]![0]!["id"] = "claude-opus-9"));             // codex list must not
    }

    [Fact]
    public void ModelCountAndPlansAreBounded()
    {
        Assert.Null(Decode(n => n["claude"]!["models"] = new JsonArray()));
        Assert.Null(Decode(n => n["codex"]!.AsObject().Remove("models")));
        Assert.Null(Decode(n =>
        {
            var many = new JsonArray();
            for (var i = 0; i < 501; i++) many.Add(JsonNode.Parse($$"""{"id":"claude-m{{i}}","input":1,"output":1,"cacheRead":1}"""));
            n["claude"]!["models"] = many;
        }));
        Assert.Null(Decode(n => n["claude"]!["plans"]![0]!["name"] = ""));
        Assert.Null(Decode(n => n["claude"]!["plans"]![0]!["monthly"] = -5));
        Assert.Null(Decode(n => n["claude"]!["plans"]![0]!["match"] = "Has Space"));
    }

    [Fact]
    public void UpdatedMustBeARealRecentDay()
    {
        Assert.Null(Decode(n => n["updated"] = "2026-13-40"));
        Assert.Null(Decode(n => n["updated"] = "yesterday"));
        Assert.Null(Decode(n => n["updated"] = "2001-01-01"));
        Assert.Null(Decode(n => n.AsObject().Remove("updated")));
    }

    [Fact]
    public void UnknownExtraFieldsAreIgnored() =>
        Assert.NotNull(Decode(n => { n["future"] = JsonNode.Parse("""{"a":1}"""); n["claude"]!["models"]![0]!["extra"] = 1; }));

    [Fact]
    public void FieldsLeftOutFallBackToInputPricing()
    {
        var list = PriceList.Decode("""
            {"schema":1,"updated":"2026-01-01",
             "claude":{"webSearch":0.01,"usOnlyMultiplier":1.1,"models":[{"id":"claude-x-1","input":3,"output":9,"cacheRead":0.3}],"plans":[]},
             "codex":{"models":[{"id":"gpt-x","input":2,"output":8,"cacheRead":0.5}],"plans":[]}}
            """u8)!;
        var m = list.Claude[0];
        Assert.Equal(3, m.CacheWrite);
        Assert.Equal(3, m.CacheWriteLong);
        Assert.Equal(1, m.FastMultiplier);
        Assert.Null(m.LongContext);
    }

    [Fact]
    public void NewerListWinsAndTheDownloadedOneWinsATie()
    {
        var shipped = PriceList.Empty with { Updated = new DateOnly(2026, 1, 1) };
        var older = PriceList.Empty with { Updated = new DateOnly(2025, 1, 1) };
        var sameDay = PriceList.Empty with { Updated = new DateOnly(2026, 1, 1), WebSearch = 0.5 };
        Assert.Same(shipped, PriceList.Newer(shipped, older));
        Assert.Same(sameDay, PriceList.Newer(shipped, sameDay));
        Assert.Same(shipped, PriceList.Newer(shipped, null));
        Assert.Same(older, PriceList.Newer(null, older));
    }

    [Fact]
    public void SourceInstallsOnlyNewerValidListsAndPersistsThem()
    {
        var dir = T.TempDir();
        var cache = Path.Combine(dir, "agent-prices.json");
        var source = new PriceSource(cache);
        var shippedDate = source.Current.Updated;

        Assert.False(source.Install(Mutate(n => n["updated"] = shippedDate.AddDays(-1).ToString("yyyy-MM-dd"))));   // older: ignored
        Assert.False(source.Install(Mutate(n => n["schema"] = 2)));                                                   // invalid: ignored
        Assert.False(File.Exists(cache));

        var newer = Mutate(n => { n["updated"] = shippedDate.AddDays(3).ToString("yyyy-MM-dd"); n["claude"]!["webSearch"] = 0.02; });
        Assert.True(source.Install(newer));
        Assert.Equal(shippedDate.AddDays(3), source.Current.Updated);
        Assert.Equal(0.02, source.Current.WebSearch);

        var reloaded = new PriceSource(cache);   // next start picks the downloaded list up
        Assert.Equal(shippedDate.AddDays(3), reloaded.Current.Updated);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void RawUrlPointsAtTheProjectsOwnRepo() =>
        Assert.Equal("https://raw.githubusercontent.com/me/Lintel/main/src/Lintel.Agents/Resources/agent-prices.json", PriceSource.Url("me", "Lintel"));
}
