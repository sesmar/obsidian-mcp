using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObsidianMcp.Server.Records;

/// <summary>
/// Describes the structured collections in the vault (Client → Matter → Work Product), the knowledge base and
/// workflow folders. Loaded from {vault}/.obsidian-mcp/schema.json; the defaults below are used when absent.
/// Every property name, folder, status and type is configurable so the server can follow an existing vault.
/// </summary>
public sealed class RecordSchema
{
    public const string Client = "client";
    public const string Matter = "matter";
    public const string WorkProduct = "work_product";

    public Dictionary<string, CollectionSchema> Collections { get; set; } = DefaultCollections();

    /// <summary>Docket property names on Work Product records.</summary>
    public WorkProductFields Fields { get; set; } = new();

    /// <summary>Knowledge-base categories and the folders that hold them.</summary>
    public Dictionary<string, string> KnowledgeFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["case_law"] = "Knowledge/Case Law",
        ["legal_authorities"] = "Knowledge/Legal Authorities",
        ["prosecution_strategies"] = "Knowledge/Prosecution Strategies",
        ["arguments"] = "Knowledge/Argument Library",
        ["firm_procedures"] = "Knowledge/Firm Procedures",
        ["research"] = "Knowledge/Research",
    };

    /// <summary>Properties surfaced with knowledge results (citations, sources, verification).</summary>
    public List<string> CitationProperties { get; set; } =
        ["citation", "pinpoint", "court", "date_decided", "source_url", "verified_date", "authority_type"];

    public string WorkflowsFolder { get; set; } = "Workflows";
    /// <summary>Workflow property listing the Work Product types it applies to.</summary>
    public string WorkflowAppliesToProperty { get; set; } = "applies_to";
    public string TemplatesFolder { get; set; } = "Templates";

    /// <summary>Client note headings returned as "preferences" in matter / work product context.</summary>
    public List<string> ClientContextSections { get; set; } =
        ["Drafting Preferences", "Reporting Requirements", "Prosecution Guidelines", "Client Guidelines", "Contacts"];

    /// <summary>Headings summarised from completed Work Products in matter history.</summary>
    public List<string> HistorySections { get; set; } = ["Strategy", "Outcome", "Summary"];

    /// <summary>Maximum characters of any single section included in a context response.</summary>
    public int ContextSectionMaxChars { get; set; } = 4000;

    public int StaleAfterDays { get; set; } = 14;

    [JsonIgnore] public CollectionSchema ClientCollection => Collections[Client];
    [JsonIgnore] public CollectionSchema MatterCollection => Collections[Matter];
    [JsonIgnore] public CollectionSchema WorkProductCollection => Collections[WorkProduct];

    private static Dictionary<string, CollectionSchema> DefaultCollections() => new(StringComparer.OrdinalIgnoreCase)
    {
        [Client] = new CollectionSchema
        {
            Label = "Client",
            Folder = "Clients",
            IdProperty = "client_code",
            TitleProperty = "client_name",
            PathTemplate = "Clients/{client_code} - {client_name}",
            Template = "Templates/Client.md",
            Required = ["client_code", "client_name", "status"],
            StatusProperty = "status",
            ArchivedStatus = "Archived",
            InactiveStatuses = ["Inactive", "Archived"],
            Properties = new(StringComparer.OrdinalIgnoreCase)
            {
                ["client_code"] = new() { Type = "string", Description = "Stable client identifier, e.g. DEMO" },
                ["client_name"] = new() { Type = "string" },
                ["status"] = new() { Type = "enum", Values = ["Active", "Inactive", "Prospective", "Archived"], Default = "Active" },
                ["record_type"] = new() { Type = "string", Default = "Client" },
                ["created"] = new() { Type = "date" },
            },
        },
        [Matter] = new CollectionSchema
        {
            Label = "Matter",
            Folder = "Matters",
            IdProperty = "docket_number",
            TitleProperty = "title",
            ParentCollection = Client,
            ParentProperty = "client",
            PathTemplate = "Matters/{client_id}/{docket_number}",
            Template = "Templates/Matter.md",
            Required = ["docket_number", "client", "status"],
            StatusProperty = "status",
            ArchivedStatus = "Archived",
            InactiveStatuses = ["Closed", "Abandoned", "Archived"],
            NotesHeading = "Matter Notes",
            RelatedProperty = "related_matters",
            FamilyProperty = "family",
            Properties = new(StringComparer.OrdinalIgnoreCase)
            {
                ["docket_number"] = new() { Type = "string", Description = "Firm docket number, e.g. DEMO-101-A" },
                ["client"] = new() { Type = "link" },
                ["title"] = new() { Type = "string" },
                ["status"] = new() { Type = "enum", Values = ["Active", "Pending", "Allowed", "Issued", "Abandoned", "Closed", "Archived"], Default = "Active" },
                ["matter_type"] = new() { Type = "enum", Values = ["Utility", "Provisional", "PCT", "Design", "Continuation", "Divisional", "CIP", "Foreign", "Other"] },
                ["jurisdiction"] = new() { Type = "string" },
                ["application_number"] = new() { Type = "string" },
                ["filing_date"] = new() { Type = "date" },
                ["priority_date"] = new() { Type = "date" },
                ["publication_number"] = new() { Type = "string" },
                ["patent_number"] = new() { Type = "string" },
                ["examiner"] = new() { Type = "string" },
                ["art_unit"] = new() { Type = "string" },
                ["family"] = new() { Type = "string" },
                ["related_matters"] = new() { Type = "list" },
                ["record_type"] = new() { Type = "string", Default = "Matter" },
                ["created"] = new() { Type = "date" },
            },
        },
        [WorkProduct] = new CollectionSchema
        {
            Label = "Work Product",
            Folder = "Work Products",
            IdProperty = "work_product_id",
            TitleProperty = "title",
            ParentCollection = Matter,
            ParentProperty = "matter",
            PathTemplate = "Work Products/{matter_id}/{work_product_id} {work_product_type}",
            Template = "Templates/Work Product.md",
            Required = ["work_product_id", "matter", "work_product_type", "status"],
            StatusProperty = "status",
            ArchivedStatus = "Archived",
            InactiveStatuses = ["Completed", "Filed", "Cancelled", "Archived"],
            ProtectedProperties = ["due_date"],
            NotesHeading = "Working Notes",
            ChecklistHeading = "Checklist",
            Properties = new(StringComparer.OrdinalIgnoreCase)
            {
                ["work_product_id"] = new() { Type = "string" },
                ["matter"] = new() { Type = "link" },
                ["title"] = new() { Type = "string" },
                ["work_product_type"] = new()
                {
                    Type = "enum",
                    Values =
                    [
                        "Non-Final Office Action Response", "Final Office Action Response", "Restriction Response",
                        "Application Drafting", "Notice of Allowance", "Inventor Interview", "Examiner Interview",
                        "Information Disclosure Statement", "Appeal", "Foreign Filing", "Client Report", "Other",
                    ],
                },
                ["status"] = new()
                {
                    Type = "enum",
                    Values = ["Not Started", "In Progress", "Waiting", "In Review", "Ready for Filing", "Filed", "Completed", "Cancelled", "Archived"],
                    Default = "Not Started",
                },
                ["priority"] = new() { Type = "enum", Values = ["Urgent", "High", "Normal", "Low"], Default = "Normal" },
                ["due_date"] = new() { Type = "date", Description = "Deadline (copied from the firm docketing system). Protected." },
                ["target_date"] = new() { Type = "date", Description = "Internal completion target" },
                ["follow_up_date"] = new() { Type = "date" },
                ["assigned_date"] = new() { Type = "date" },
                ["completed_date"] = new() { Type = "date" },
                ["next_action"] = new() { Type = "string" },
                ["waiting_on"] = new() { Type = "string" },
                ["record_type"] = new() { Type = "string", Default = "Work Product" },
                ["created"] = new() { Type = "date" },
            },
        },
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static RecordSchema Load(string? file)
    {
        RecordSchema schema;
        if (file is not null && File.Exists(file))
        {
            try
            {
                schema = JsonSerializer.Deserialize<RecordSchema>(File.ReadAllText(file), JsonOptions) ?? new RecordSchema();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid schema file {file}: {ex.Message}");
            }
        }
        else schema = new RecordSchema();

        foreach (var required in new[] { Client, Matter, WorkProduct })
            if (!schema.Collections.ContainsKey(required))
                throw new InvalidOperationException($"Schema must define the '{required}' collection.");
        // Make dictionaries case-insensitive regardless of how they were deserialized.
        schema.Collections = new(schema.Collections, StringComparer.OrdinalIgnoreCase);
        foreach (var c in schema.Collections.Values) c.Properties = new(c.Properties, StringComparer.OrdinalIgnoreCase);
        schema.KnowledgeFolders = new(schema.KnowledgeFolders, StringComparer.OrdinalIgnoreCase);
        return schema;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

public sealed class CollectionSchema
{
    public string Label { get; set; } = "";
    /// <summary>Vault folder containing the records (all notes below it belong to the collection).</summary>
    public string Folder { get; set; } = "";
    public string IdProperty { get; set; } = "id";
    public string? TitleProperty { get; set; }
    /// <summary>
    /// Path for new records, without ".md". Tokens: {property} from the record, plus {parent_id} and
    /// {&lt;parent collection&gt;_id} (e.g. {client_id}, {matter_id}).
    /// </summary>
    public string? PathTemplate { get; set; }
    /// <summary>Note used as the starting content for new records ({{property}} placeholders are filled).</summary>
    public string? Template { get; set; }
    public List<string> Required { get; set; } = [];
    public Dictionary<string, PropertySchema> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ParentCollection { get; set; }
    public string? ParentProperty { get; set; }
    public string StatusProperty { get; set; } = "status";
    /// <summary>Statuses meaning "no longer active" (completed, closed, archived…).</summary>
    public List<string> InactiveStatuses { get; set; } = [];
    public string? ArchivedStatus { get; set; }
    /// <summary>Properties that only dedicated, explicitly confirmed operations may change (e.g. due_date).</summary>
    public List<string> ProtectedProperties { get; set; } = [];
    public string? NotesHeading { get; set; } = "Notes";
    public string? ChecklistHeading { get; set; }
    public string? RelatedProperty { get; set; }
    public string? FamilyProperty { get; set; }
}

public sealed class PropertySchema
{
    /// <summary>string, enum, date, number, boolean, list, link</summary>
    public string Type { get; set; } = "string";
    public List<string>? Values { get; set; }
    public string? Default { get; set; }
    public string? Description { get; set; }
}

public sealed class WorkProductFields
{
    public string Type { get; set; } = "work_product_type";
    public string Priority { get; set; } = "priority";
    public string DueDate { get; set; } = "due_date";
    public string TargetDate { get; set; } = "target_date";
    public string FollowUpDate { get; set; } = "follow_up_date";
    public string CompletedDate { get; set; } = "completed_date";
    public string NextAction { get; set; } = "next_action";
    public string WaitingOn { get; set; } = "waiting_on";
    /// <summary>Status values with a docket meaning.</summary>
    public string WaitingStatus { get; set; } = "Waiting";
    public string ReadyForFilingStatus { get; set; } = "Ready for Filing";
    public string CompletedStatus { get; set; } = "Completed";
    public string ReopenedStatus { get; set; } = "In Progress";
}
