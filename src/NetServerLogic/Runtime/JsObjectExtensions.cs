using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Microsoft.AspNetCore.Http;
using NetServerLogic.Options;

namespace NetServerLogic.Runtime;

/// <summary>
/// A native Jint <see cref="ObjectInstance"/> (inheriting <c>Object.prototype</c>) that preserves original
/// own property keys for <c>Object.keys</c>, destructuring, and spread while falling back to case-insensitive
/// property lookup for dot and bracket access.
/// </summary>
public sealed class CaseInsensitiveJsObject : ObjectInstance
{
    private readonly Dictionary<string, JsString> _canonicalKeys = new(StringComparer.OrdinalIgnoreCase);

    public CaseInsensitiveJsObject(Engine engine)
        : base(engine)
    {
        Prototype = engine.Intrinsics.Object.PrototypeObject;
    }

    public void SetCaseInsensitiveProperty(string name, JsValue value)
    {
        _canonicalKeys[name] = new JsString(name);
        FastSetDataProperty(name, value);
    }

    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        var descriptor = base.GetOwnProperty(property);
        if (descriptor != PropertyDescriptor.Undefined)
        {
            return descriptor;
        }

        if (property.IsString() &&
            _canonicalKeys.TryGetValue(property.AsString(), out var canonicalKey))
        {
            return base.GetOwnProperty(canonicalKey);
        }

        return PropertyDescriptor.Undefined;
    }

    protected override OwnPropertyProbe ProbeOwnProperty(JsValue property)
    {
        var probe = base.ProbeOwnProperty(property);
        if (probe != OwnPropertyProbe.Missing)
        {
            return probe;
        }

        if (property.IsString() &&
            _canonicalKeys.TryGetValue(property.AsString(), out var canonicalKey))
        {
            return base.ProbeOwnProperty(canonicalKey);
        }

        return OwnPropertyProbe.Missing;
    }

    protected override void SetOwnProperty(JsValue property, PropertyDescriptor desc)
    {
        if (property.IsString())
        {
            var str = property.AsString();
            _canonicalKeys[str] = new JsString(str);
        }

        base.SetOwnProperty(property, desc);
    }

    public override void RemoveOwnProperty(JsValue property)
    {
        if (property.IsString())
        {
            var str = property.AsString();
            if (_canonicalKeys.TryGetValue(str, out var canonical))
            {
                _canonicalKeys.Remove(str);
                base.RemoveOwnProperty(canonical);
                return;
            }
        }

        base.RemoveOwnProperty(property);
    }
}

/// <summary>
/// Helpers to project .NET dictionaries, ASP.NET Core collections, and mock user models into native Jint objects.
/// </summary>
public static class JsObjectExtensions
{
    public static CaseInsensitiveJsObject ToJsObject(this IQueryCollection query, Engine engine)
    {
        var obj = new CaseInsensitiveJsObject(engine);
        foreach (var kvp in query)
        {
            obj.SetCaseInsensitiveProperty(kvp.Key, kvp.Value.ToString());
        }

        return obj;
    }

    public static CaseInsensitiveJsObject ToJsObject(this IHeaderDictionary headers, Engine engine)
    {
        var obj = new CaseInsensitiveJsObject(engine);
        foreach (var kvp in headers)
        {
            obj.SetCaseInsensitiveProperty(kvp.Key, kvp.Value.ToString());
        }

        return obj;
    }

    public static JsObject ToJsObject(this IDictionary<string, string> values, Engine engine)
    {
        var obj = new JsObject(engine);
        foreach (var kvp in values)
        {
            obj.FastSetDataProperty(kvp.Key, kvp.Value);
        }

        return obj;
    }

    public static CaseInsensitiveJsObject ToCaseInsensitiveJsObject(this IDictionary<string, string> values, Engine engine)
    {
        var obj = new CaseInsensitiveJsObject(engine);
        foreach (var kvp in values)
        {
            obj.SetCaseInsensitiveProperty(kvp.Key, kvp.Value);
        }

        return obj;
    }

    public static JsObject ToJsObject(this MockUserOptions user, Engine engine)
    {
        var roleValues = new JsValue[user.Roles.Count];
        for (var i = 0; i < user.Roles.Count; i++)
        {
            roleValues[i] = user.Roles[i];
        }

        var rolesArray = new JsArray(engine, roleValues);

        var userObj = new JsObject(engine);
        userObj.FastSetDataProperty("contactid", user.ContactId);
        userObj.FastSetDataProperty("fullname", user.FullName);
        userObj.FastSetDataProperty("emailaddress1", user.EmailAddress1);
        userObj.FastSetDataProperty("roles", rolesArray);
        return userObj;
    }

    public static IDictionary<string, object?>? ToHeaderDictionary(this JsValue? value)
    {
        if (value is null || value.IsNull() || value.IsUndefined())
        {
            return null;
        }

        if (!value.IsObject())
        {
            throw new ArgumentException("HTTP headers argument must be an object.", nameof(value));
        }

        var obj = value.AsObject();
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in obj.GetOwnProperties())
        {
            if (kvp.Key.IsString())
            {
                var propVal = obj.Get(kvp.Key);
                dict[kvp.Key.AsString()] = propVal.IsNull() || propVal.IsUndefined()
                    ? null
                    : propVal.ToObject();
            }
        }

        return dict;
    }
}
