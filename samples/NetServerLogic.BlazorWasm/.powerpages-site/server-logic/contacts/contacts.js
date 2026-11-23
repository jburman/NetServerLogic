// Power Pages Server Logic: /_api/serverlogics/contacts
// Locally via NetServerLogic, Dataverse calls run under the configured Entra ID App User
// (so all contacts visible to that App User are returned). When deployed to Power Pages,
// Power Pages table permissions automatically scope records to the signed-in portal user.

function get() {
    var query = "$select=contactid,firstname,lastname,fullname,emailaddress1&$orderby=fullname asc&$top=50";
    var rawJson = Server.Connector.Dataverse.RetrieveMultipleRecords("contacts", query);
    return rawJson;
}

function patch() {
    var payload = JSON.parse(Server.Context.Body);
    if (!payload || !payload.contactid) {
        throw new Error("Missing required field: contactid");
    }

    var updateBody = {
        firstname: payload.firstname || "",
        lastname: payload.lastname || "",
        emailaddress1: payload.emailaddress1 || ""
    };

    Server.Connector.Dataverse.UpdateRecord("contacts", payload.contactid, JSON.stringify(updateBody));

    var updatedJson = Server.Connector.Dataverse.RetrieveRecord(
        "contacts",
        payload.contactid,
        "$select=contactid,firstname,lastname,fullname,emailaddress1"
    );

    return updatedJson;
}
