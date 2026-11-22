using NetServerLogic.Validation;

namespace NetServerLogic.Tests.Validation;

public sealed class AcornimaScriptValidatorTests
{
    private readonly AcornimaScriptValidator _validator = new();

    [Theory]
    [InlineData("function get() { var o = { x: 1 }; delete o.x; return 'ok'; }", "RTSL01-DELETE")]
    [InlineData("function get() { with ({ a: 1 }) { return String(a); } }", "RTSL01-WITH")]
    [InlineData("function get() { debugger; return 'ok'; }", "RTSL01-DEBUGGER")]
    [InlineData("import x from 'mod'; function get() { return x; }", "RTSL01-MODULE")]
    [InlineData("export function get() { return 'ok'; }", "RTSL01-MODULE")]
    [InlineData("function get() { var m = require('fs'); return m; }", "RTSL01-MODULE")]
    [InlineData("function get() { return typeof import('fs'); }", "RTSL01-MODULE")]
    [InlineData("function get() { Object.prototype.x = 1; return 'ok'; }", "RTSL01-METAPROG")]
    [InlineData("function get() { var x = {}; return String(x.__proto__); }", "RTSL01-METAPROG")]
    [InlineData("function get() { var x = {}; return String(x['__proto__']); }", "RTSL01-METAPROG")]
    [InlineData("function get() { var p = new Proxy({}, {}); return 'ok'; }", "RTSL01-METAPROG")]
    [InlineData("function get() { return String(Reflect.ownKeys({})); }", "RTSL01-METAPROG")]
    [InlineData("function get() { return String(arguments.callee); }", "RTSL01-METAPROG")]
    [InlineData("function get() { return String(Symbol.for('x')); }", "RTSL01-METAPROG")]
    [InlineData("function get() { return String(Object.getPrototypeOf({})); }", "RTSL01-METAPROG")]
    [InlineData("function get() { return String(Object.setPrototypeOf({}, null)); }", "RTSL01-METAPROG")]
    [InlineData("function get() { return String(this.constructor); }", "RTSL01-METAPROG")]
    [InlineData("function get() { return String(''.constructor.constructor); }", "RTSL01-METAPROG")]
    [InlineData("function get() { return String(eval('1+1')); }", "RTSL01-DYNAMIC-CODE")]
    [InlineData("function get() { var f = new Function('return 1'); return String(f()); }", "RTSL01-DYNAMIC-CODE")]
    [InlineData("function get() { console.log('hi'); return 'ok'; }", "RTSL01-GLOBALS")]
    [InlineData("async function get() { return await fetch('https://example.com'); }", "RTSL01-GLOBALS")]
    [InlineData("function get() { return String(new XMLHttpRequest()); }", "RTSL01-GLOBALS")]
    [InlineData("function get() { setTimeout(function() {}, 10); return 'ok'; }", "RTSL01-GLOBALS")]
    [InlineData("function get() { setInterval(function() {}, 10); return 'ok'; }", "RTSL01-GLOBALS")]
    [InlineData("function get() { globalThis.fetch('https://example.com'); return 'ok'; }", "RTSL01-GLOBALS")]
    [InlineData("async function get() { return await Server.Connector.Dataverse.RetrieveRecord('accounts', '1'); }", "RTSL01-SYNC-AWAIT")]
    [InlineData("function helper() { return 'not a verb'; }", "RTSL01-NO-HANDLER")]
    public void Validate_ProhibitedConstructs_ProduceExpectedErrorRule(string code, string expectedRuleId)
    {
        var result = _validator.Validate(code, "test");

        Assert.False(result.IsValid(strictValidatorMode: false));
        Assert.Contains(result.Errors, d => d.RuleId == expectedRuleId);
    }

    [Fact]
    public void Validate_StringsCommentsShadowingAndObjectKeys_DoNotTriggerFalsePositives()
    {
        const string code = """
            // delete obj.x; eval('1'); require('fs'); console.log('hi'); prototype
            /* __proto__ setTimeout XMLHttpRequest Proxy Reflect */
            function get(console) {
                var msg = "delete eval import require prototype __proto__ fetch setTimeout";
                var payload = {
                    delete: true,
                    fetch: "url",
                    console: console || "ok",
                    prototype: 123
                };
                payload.fetch = msg;
                return JSON.stringify(payload);
            }
            """;

        var result = _validator.Validate(code, "safe");

        Assert.True(result.IsValid(strictValidatorMode: true));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Validate_Warnings_PassNormalModeAndFailStrictMode()
    {
        const string code = """
            const helper = (x) => x * 2;
            function get() {
                var items = [1, 2, 3].map(n => helper(n));
                return JSON.stringify(items);
            }
            """;

        var result = _validator.Validate(code, "warnings");

        Assert.False(result.HasErrors);
        Assert.True(result.IsValid(strictValidatorMode: false));
        Assert.False(result.IsValid(strictValidatorMode: true));
        Assert.Contains(result.Warnings, d => d.RuleId == "RTSL_WARN-ARROW-CONST");
        Assert.Contains(result.Warnings, d => d.RuleId == "RTSL_WARN-ARRAY-CALLBACK");
    }

    [Fact]
    public void Validate_Section10GoldenScript_PassesCleanlyWithZeroDiagnostics()
    {
        const string goldenScript = """
            function get() {
                var minRevenue = Server.Context.QueryParameters.minRevenue || "0";
                var user = Server.User;

                Server.Logger.Log("Fetching accounts for user: " + (user ? user.fullname : "anonymous"));

                var query = "$select=accountid,name,revenue&$filter=revenue ge " + minRevenue + "&$orderby=revenue desc&$top=10";
                var rawJson = Server.Connector.Dataverse.RetrieveMultipleRecords("accounts", query);
                var parsed = JSON.parse(rawJson);

                return JSON.stringify({
                    requestedBy: user ? user.contactid : null,
                    count: parsed.value.length,
                    items: parsed.value
                });
            }

            async function post() {
                var payload = JSON.parse(Server.Context.Body);
                var externalBase = Server.EnvironmentVariable.get("cr123_ExternalApiBaseUrl");

                var externalResp = await Server.Connector.HttpClient.GetAsync(externalBase + "/health");
                var created = Server.Connector.Dataverse.CreateRecord("accounts", JSON.stringify({
                    name: payload.name,
                    revenue: payload.revenue
                }));

                return JSON.stringify({
                    created: created,
                    healthCheck: externalResp
                });
            }
            """;

        var result = _validator.Validate(goldenScript, "accounts-summary");

        Assert.True(result.IsValid(strictValidatorMode: true));
        Assert.Empty(result.Diagnostics);
    }
}
