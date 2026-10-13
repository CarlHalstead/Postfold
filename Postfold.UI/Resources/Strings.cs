using System;
using System.Globalization;
using System.Resources;

namespace Postfold.UI.Resources;

public static class Strings
{
    private static readonly ResourceManager Resources = new ResourceManager("Postfold.UI.Resources.Strings", typeof(Strings).Assembly);

    public static string NewMail => Get("NewMail");
    public static string AccountActions => Get("AccountActions");
    public static string MoreSettings => Get("MoreSettings");
    public static string AllMail => Get("AllMail");
    public static string Unread => Get("Unread");
    public static string Work => Get("Work");
    public static string MoreActions => Get("MoreActions");
    public static string FiltersAndMore => Get("FiltersAndMore");
    public static string MessageDetails => Get("MessageDetails");
    public static string SearchScopeHint => Get("SearchScopeHint");
    public static string SelectMessage => Get("SelectMessage");
    public static string MarkRead => Get("MarkRead");
    public static string MarkUnread => Get("MarkUnread");
    public static string Attachments => Get("Attachments");
    public static string AttachmentMark => Get("AttachmentMark");
    public static string MessageActions => Get("MessageActions");
    public static string Today => Get("Today");
    public static string Yesterday => Get("Yesterday");
    public static string Email => Get("Email");
    public static string SharedCredentials => Get("SharedCredentials");
    public static string SharedPassword => Get("SharedPassword");
    public static string LoginHint => Get("LoginHint");
    public static string ServerSettings => Get("ServerSettings");
    public static string ServerHint => Get("ServerHint");
    public static string AdvancedLogin => Get("AdvancedLogin");
    public static string ReceivingSettings => Get("ReceivingSettings");
    public static string SendingSettings => Get("SendingSettings");
    public static string ContinueServers => Get("ContinueServers");
    public static string BackLogin => Get("BackLogin");
    public static string SendingOverrideHint => Get("SendingOverrideHint");
    public static string CredentialUsernames => Get("CredentialUsernames");
    public static string ReadState => Get("ReadState");
    public static string UnreadState => Get("UnreadState");
    public static string Flagged => Get("Flagged");

    public static string Bytes => Get("Bytes");
    public static string NavigationResize => Get("NavigationResize");
    public static string PreviewResize => Get("PreviewResize");
    public static string Credentials => Get("Credentials");
    public static string Move => Get("Move");
    public static string Commands => Get("Commands");
    public static string DesktopSettings => Get("DesktopSettings");
    public static string HtmlRead => Get("HtmlRead");
    public static string LoadImages => Get("LoadImages");
    public static string CheckAttachments => Get("CheckAttachments");
    public static string IdentitySettings => Get("IdentitySettings");
    public static string RenameAccount => Get("RenameAccount");
    public static string RemoveAccount => Get("RemoveAccount");
    public static string NoMessages => Get("NoMessages");
    public static string Cancel => Get("Cancel");
    public static string Compose => Get("Compose");
    public static string Search => Get("Search");
    public static string Refresh => Get("Refresh");
    public static string Setup => Get("Setup");
    public static string More => Get("More");
    public static string Retrieve => Get("Retrieve");
    public static string Read => Get("Read");
    public static string Flag => Get("Flag");
    public static string Archive => Get("Archive");
    public static string Trash => Get("Trash");
    public static string ReplyAll => Get("ReplyAll");
    public static string Reply => Get("Reply");
    public static string Forward => Get("Forward");
    public static string SearchHint => Get("SearchHint");
    public static string HtmlUnavailable => Get("HtmlUnavailable");
    public static string UnifiedInbox => Get("UnifiedInbox");
    public static string UnifiedSearch => Get("UnifiedSearch");
    public static string Ready => Get("Ready");
    public static string Missing => Get("Missing");
    public static string NoResults => Get("NoResults");
    public static string Pending => Get("Pending");
    public static string Scope => Get("Scope");
    public static string Messages => Get("Messages");
    public static string Preview => Get("Preview");
    public static string Destination => Get("Destination");
    public static string Sender => Get("Sender");
    public static string Recipients => Get("Recipients");
    public static string Subject => Get("Subject");
    public static string Body => Get("Body");
    public static string Signature => Get("Signature");
    public static string Send => Get("Send");
    public static string Save => Get("Save");
    public static string Attach => Get("Attach");
    public static string Saved => Get("Saved");
    public static string Saving => Get("Saving");
    public static string DraftFailed => Get("DraftFailed");
    public static string Conflict => Get("Conflict");
    public static string Privacy => Get("Privacy");
    public static string SessionOnly => Get("SessionOnly");
    public static string TestEndpoints => Get("TestEndpoints");
    public static string IncomingHost => Get("IncomingHost");
    public static string OutgoingHost => Get("OutgoingHost");
    public static string Port => Get("Port");
    public static string Username => Get("Username");
    public static string Password => Get("Password");
    public static string Name => Get("Name");
    public static string DraftMailbox => Get("DraftMailbox");
    public static string Drafts => Get("Drafts");
    public static string Synchronise => Get("Synchronise");
    public static string Synthetic => Get("Synthetic");
    public static string Settings => Get("Settings");

    public static string Get(string key)
    {
        return Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }

    public static string Describe(Enum value)
    {
        return Get(value.GetType().Name + "." + value);
    }
}
