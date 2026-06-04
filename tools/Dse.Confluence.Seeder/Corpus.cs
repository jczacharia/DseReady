// Copyright (c) PNC Financial Services. All rights reserved.


using Bogus;
using static Dse.Confluence.Seeder.StorageFormat;

namespace Dse.Confluence.Seeder;

// Builds the space/page tree: curated pages from the seed data + a kitchen-sink showcase per space +
// Bogus synthetic leaves/blogs. Deterministic for a given RandomSeed.
public sealed class Corpus(SeederOptions options)
{
    // Lower-cased Confluence usernames (created in the instance) + real-looking display names.
    public static readonly Author Admin = new("admin", "admin");

    public static readonly IReadOnlyList<Author> Authors =
    [
        new("cpiasente", "Cathy Piasente"),
        new("ssubbiah", "Saravanan Subbiah"),
        new("dsmith", "Derik Smith"),
        new("tkomlenic", "Todd Komlenic"),
        new("eschofield", "Eric Schofield"),
        new("rchandler", "Raymond Chandler"),
        new("sgriffith", "Sam Griffith"),
        new("dkeister", "Don Keister"),
    ];

    private static readonly string[] s_labelPool =
    [
        "cyberark", "ldap", "active-directory", "openshift", "secrets", "runbook", "knowledge-base",
        "how-to", "reference", "pattern", "security", "kerberos", "gmsa", "pipeline", "helm",
        "troubleshooting", "onboarding", "harmony", "angular", "vulnerability", "sre", "deployment",
    ];

    private static readonly string[] s_jiraKeys =
        ["CSPPR-5705", "CSPPR-6243", "KEY-1", "VUF-44661228", "AGO-443591969", "HAR-2207"];

    private readonly Faker _faker = new() { Random = new Randomizer(options.RandomSeed) };

    public IReadOnlyList<SeedSpace> Build()
    {
        var spaces = new List<SeedSpace>
        {
            BuildCyberArkOps(),
            BuildDirectoryServices(),
            BuildHarmonyWeb(),
            BuildPlatformSre(),
            BuildDeployPipeline(),
        };

        foreach (SeedSpace space in spaces)
        {
            AddSyntheticContent(space);
        }

        return spaces;
    }

    private Author Pick() => _faker.PickRandom<Author>(Authors);

    private List<string> PickLabels(int min, int max) =>
        _faker.PickRandom(s_labelPool, _faker.Random.Int(min, max)).ToList();

    // ---------------------------------------------------------------- CyberArk Ops (CYOPS)

    private SeedSpace BuildCyberArkOps()
    {
        var space = new SeedSpace
        {
            Key = "CYOPS",
            Name = "CyberArk Ops",
            Description = "Operational runbooks, links and knowledge base for CyberArk PAM/AAM at PNC.",
        };

        SeedPage home = Home("CyberArk Operations",
            "Landing page for the CyberArk operations team — vaulting, AAM, and the OpenShift secrets pipeline.",
            ["CyberArk Links", "CyberArk Knowledge Base", "CyberArk OCP Secrets Pipeline", "Showcase: Storage Format Coverage"]);

        home.Children.Add(CyberArkLinks());
        home.Children.Add(CyberArkKnowledgeBase());
        home.Children.Add(CyberArkOpenShiftPipeline());
        home.Children.Add(Showcase(space.Key, "CyberArk"));
        space.Pages.Add(home);
        return space;
    }

    private static SeedPage CyberArkLinks()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "CyberArk PVS", ExternalLink("https://rpv.pncint.net/PasswordVault/logon.aspx", "Password Vault"),
                "Privileged vault sign-in.",
            },
            new[]
            {
                "AAM PROD", ExternalLink("https://aam.pncint.net/PasswordVault/v10/logon/radius", "AAM Prod"),
                "Application Access Manager (prod).",
            },
            new[]
            {
                "AAM QA", ExternalLink("https://aam-qa.pncint.net/PasswordVault/v10/logon/ldap", "AAM QA"),
                "Application Access Manager (QA).",
            },
            new[]
            {
                "Secrets Broker", PageLink("CyberArk OCP Secrets Pipeline", "Secrets Broker — Customer Information"),
                "Internal pipeline docs.",
            },
            new[]
            {
                "Knowledge Base", PageLink("CyberArk Knowledge Base", "CyberArk Troubleshooting"), "Error/instruction reference.",
            },
            new[]
            {
                "JIRA (Keymasters)", ExternalLink("https://jira.pncint.net/projects/KEY/issues/KEY-1", "KEY board"),
                "Engineering backlog.",
            },
        };

        string body = string.Concat(
            Para("Quick reference of CyberArk links, wiki pages, tools and job aids. " + Emoticon("info")),
            Table(new[] { "Name", "Link", "Description / Instruction" }, rows),
            Para("See also " + RootRelativeLink("/spaces/CYOPS/overview", "the CyberArk Ops space overview") +
                 " and the " + AttachmentLink("cyberark-links.txt", "exported link list") + "."));

        var page = new SeedPage
            { Title = "CyberArk Links", BodyStorage = body, Author = new Author("cpiasente", "Cathy Piasente") };
        page.Labels.AddRange(["cyberark", "reference", "knowledge-base"]);
        page.Attachments.Add(new SeedAttachment("cyberark-links.txt",
            Assets.Text("CyberArk PVS, AAM PROD/QA/UAT/RND, Secrets Broker, JIRA Keymasters"),
            "text/plain", "Exported link list"));
        return page;
    }

    private static SeedPage CyberArkKnowledgeBase()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "suspended - contact system administrator", "User cannot authenticate into CyberArk.",
                "Remove password violations from PrivateArk.",
            },
            new[]
            {
                "Authentication failure for User [PNCID]", "PVS login failure.",
                "Confirm the user holds the PVS Privileged User entitlement.",
            },
            new[]
            {
                "EXT01::Cannot contact the LDAP server", "OUD reconcile failure.",
                "Check spelling of Username, Address and UserDN fields.",
            },
            new[]
            {
                "Reconcilepass ... Timeout (240) elapsed", "Unix/Linux account times out.",
                "Verify the PVSRECON account; confirm casing matches the server.",
            },
        };

        string body = string.Concat(
            Toc(),
            Heading(level: 2, "Overview"),
            Para("Common CyberArk errors and the corrective instructions. " +
                 Status("Yellow", "Living document") + " Maintained by " + UserMention("placeholder-key") + "."),
            Panel("note", "Before you start", Para("Always confirm a reconcile account is associated before escalating.")),
            Heading(level: 2, "Error reference"),
            Table(new[] { "Error", "Description", "Instructions" }, rows),
            Heading(level: 2, "OUD specifics"),
            Para("CyberArk cannot manage an OUD account if it has " + InlineCode("exe") + " anywhere in the name."),
            Expand("Show raw error sample",
                NoFormat("CACPM336E reconcilepass password process terminated. Timeout (240) elapsed")));

        var page = new SeedPage
            { Title = "CyberArk Knowledge Base", BodyStorage = body, Author = new Author("cpiasente", "Cathy Piasente") };
        page.Labels.AddRange(["cyberark", "knowledge-base", "troubleshooting"]);
        page.ExtraVersions.Add((body + Para("Update: added Midrange timeout guidance."), new Author("dsmith", "Derik Smith")));
        return page;
    }

    private static SeedPage CyberArkOpenShiftPipeline()
    {
        string body = string.Concat(
            Para("This page lays out how to update the " + InlineCode("manifest.yml") +
                 " file for deploying CyberArk secrets into OpenShift. " + Jira("CSPPR-6243")),
            Panel("info", "Run-time vs Deploy-time",
                Para("This pipeline is for " + Bold("deploy-time") + " secret loading, not run-time.")),
            Heading(level: 2, "Step 3: Update yaml manifest"),
            Para("Add an " + InlineCode("openshift-secret") + " component. Example " +
                 AttachmentLink("manifest.yml", "manifest.yml") + ":"),
            Code("yaml", Assets.SampleManifestYaml),
            Heading(level: 2, "Auto-deploy on release"),
            Para("Optionally add a stage to the " + AttachmentLink("Jenkinsfile", "Jenkinsfile") + ":"),
            Code("groovy", Assets.SampleJenkinsfile),
            Heading(level: 2, "Safe name rules"),
            Para("Crossing mnemonics is restricted; crossing environments is partially restricted:"),
            MergedCellTable(),
            Heading(level: 2, "Multi-line secrets"),
            Panel("warning", title: null, Para("CyberArk cannot store binary objects — base64-encode certs first.")),
            Para("Base64 example attached: " + AttachmentLink("sample-cert.b64", "sample-cert.b64") + "."),
            Heading(level: 2, "Related"),
            BulletList(new[]
            {
                PageLink("CyberArk Links", "CyberArk Links"),
                PageLink("CyberArk Knowledge Base", "Knowledge Base"),
                ExternalLink("https://confluence.pncint.net/spaces/AGO/pages/443591969/CyberArk+OCP+Secrets+Pipeline",
                    "Onboarding Guide"),
            }));

        var page = new SeedPage
            { Title = "CyberArk OCP Secrets Pipeline", BodyStorage = body, Author = new Author("tkomlenic", "Todd Komlenic") };
        page.Labels.AddRange(["cyberark", "openshift", "secrets", "pipeline", "helm"]);
        page.Attachments.Add(new SeedAttachment("manifest.yml", Assets.Text(Assets.SampleManifestYaml), "text/yaml",
            "Sample manifest"));
        page.Attachments.Add(new SeedAttachment("Jenkinsfile", Assets.Text(Assets.SampleJenkinsfile), "text/plain",
            "Sample Jenkinsfile"));
        page.Attachments.Add(new SeedAttachment("sample-cert.b64", Assets.Text(Assets.SampleCertB64), "text/plain",
            "Base64 cert blob"));
        page.ExtraVersions.Add((body + Para("v3: clarified helm secretsFile anchor usage."),
                                new Author("eschofield", "Eric Schofield")));
        page.ExtraVersions.Add((body + Para("v4: added QA/PROD skip example."), new Author("tkomlenic", "Todd Komlenic")));
        return page;
    }

    // ---------------------------------------------------------------- Directory Services (AGO)

    private SeedSpace BuildDirectoryServices()
    {
        var space = new SeedSpace
        {
            Key = "AGO",
            Name = "Enterprise Directory Services",
            Description = "Active Directory, LDAP and Kerberos engineering patterns and consumption guidance.",
        };

        SeedPage home = Home("Directory Services Home",
            "Authentication and authorization via Active Directory — patterns, endpoints and troubleshooting.",
            ["Active Directory Domain Services Pattern", "LDAP Authentication", "Showcase: Storage Format Coverage"]);

        home.Children.Add(ActiveDirectoryPattern());
        home.Children.Add(LdapAuthentication());
        home.Children.Add(Showcase(space.Key, "Directory Services"));
        space.Pages.Add(home);
        return space;
    }

    private static SeedPage ActiveDirectoryPattern()
    {
        var groupTypes = new List<IReadOnlyList<string>>
        {
            new[] { "Domain Local (DL)", "Resource authorization within a single domain." },
            new[] { "Global (G)", "Group principals sharing a common business function." },
            new[] { "Universal (U)", "Restricted; reserved for cross-domain scenarios." },
        };

        string body = string.Concat(
            Toc(),
            PageProperties(new[]
            {
                ("Owned by", Esc("Enterprise Technology - CPS - WIE - Active Directory")),
                ("Category", Esc("Identity & Access Mgmt => Directory Services")),
                ("Status", Status("Green", "Approved")),
            }),
            Heading(level: 2, "Introduction"),
            Para(
                "Active Directory Domain Services (AD DS) stores directory data and makes it available to users and administrators."),
            Heading(level: 2, "Security Groups"),
            Table(new[] { "Group Type", "Usage" }, groupTypes),
            Heading(level: 2, "LDAP Delegated Authentication"),
            Para("Always use the DNS hostname of the domain: " + InlineCode("ldaps://prod.intdomain.net:636") + "."),
            Panel("tip", "Bind types", BulletList(new[]
            {
                "Simple Bind over LDAPS for service accounts.",
                "Negotiate/Kerberos bind preferred when domain-joined.",
            })),
            Heading(level: 2, "Kerberos SPNs"),
            Para("Must follow RFC-compliant patterns such as " + InlineCode("HTTP/<hostname>") + " and " +
                 InlineCode("MSSQLSvc/<hostname>:<port>") + "."),
            Heading(level: 2, "References"),
            BulletList(new[]
            {
                PageLink("LDAP Authentication", "LDAP Authentication — Directory Services"),
                ExternalLink(
                    "https://learn.microsoft.com/windows-server/identity/ad-ds/get-started/virtual-dc/active-directory-domain-services-overview",
                    "AD DS overview | Microsoft Learn"),
            }));

        var page = new SeedPage
        {
            Title = "Active Directory Domain Services Pattern", BodyStorage = body, Author = new Author("dsmith", "Derik Smith"),
        };
        page.Labels.AddRange(["active-directory", "pattern", "kerberos", "gmsa", "reference"]);
        for (int v = 0; v < 4; v++)
        {
            page.ExtraVersions.Add((body + Para($"Revision {v + 17}."), new Author("dsmith", "Derik Smith")));
        }

        return page;
    }

    private static SeedPage LdapAuthentication()
    {
        string body = string.Concat(
            Heading(level: 2, "Bind account"),
            Para("The directory does not allow anonymous lookups; a service account is required."),
            Para("Example bind account names: " + InlineCode("[XQ/XS][mnemonic][environment][LDAP]") + "."),
            Heading(level: 2, "URIs / hostnames"),
            Table(new[] { "Env", "URI" }, new List<IReadOnlyList<string>>
            {
                new[] { "RND", InlineCode("ldaps://rnd.pncint.net:636/DC=RND,DC=PNCINT,DC=NET") },
                new[] { "PROD", InlineCode("ldaps://pncbank.com:636/DC=pncbank,DC=com") },
            }),
            Heading(level: 2, "LDAP filter"),
            Code("powershell",
                "Get-ADUser -LDAPFilter \"(cn=PT43033)\" -Properties @(\"memberOf\") |\n" +
                "  Select-Object -ExpandProperty \"memberOf\""),
            Heading(level: 2, "Policy limits"),
            TaskList(new[]
            {
                (true, "MaxPageSize = 1000"),
                (true, "MaxValRange = 1500"),
                (false, "Review BMC AD Replication Collector threshold"),
            }),
            Para("Back to " + PageLink("Active Directory Domain Services Pattern", "the AD pattern") + "."));

        var page = new SeedPage
            { Title = "LDAP Authentication", BodyStorage = body, Author = new Author("dsmith", "Derik Smith") };
        page.Labels.AddRange(["ldap", "active-directory", "how-to"]);
        return page;
    }

    // ---------------------------------------------------------------- Harmony-Web (HAR)

    private SeedSpace BuildHarmonyWeb()
    {
        var space = new SeedSpace
        {
            Key = "HAR",
            Name = "Harmony-Web",
            Description = "Harmony micro-frontend platform — COE docs, investigations and shared standards.",
        };

        SeedPage home = Home("Harmony-Web COE",
            "Center of excellence for the Harmony Angular micro-app platform.",
            ["Vulnerability Investigation: Exposing Library Comments - VUF-44661228", "Showcase: Storage Format Coverage"]);

        home.Children.Add(HarmonyVulnerabilityInvestigation());
        home.Children.Add(Showcase(space.Key, "Harmony"));
        space.Pages.Add(home);
        return space;
    }

    private SeedPage HarmonyVulnerabilityInvestigation()
    {
        string comparison = HtmlMacro(
            "<h1>Configuration Comparison</h1><table><thead><tr><th>Aspect</th><th>rewards</th>" +
            "<th>direct-deposit</th></tr></thead><tbody>" +
            "<tr><td>Base Image</td><td>apache-rhel8:2.4-1-0310261602</td><td>apache-rhel8:2.4-1-0210261529</td></tr>" +
            "<tr><td>optimization</td><td>explicit object</td><td>CLI defaults</td></tr></tbody></table>");

        string body = string.Concat(
            Para("The WBA team raised a vulnerability report against Harmony: Confluence-related comments inside a " +
                 "library were exposed in a production bundle. " + Jira("VUF-44661228")),
            Heading(level: 2, "Investigation"),
            Table(new[] { "App", "main.js State", "Vulnerability?" }, new List<IReadOnlyList<string>>
            {
                new[] { "set-up-direct-deposit-ui", "Compressed — fully minified", "No" },
                new[] { "rel-rewards-account-summary-ui", "Uncompressed — comments retained", Bold("Yes") },
            }),
            Para(ExternalLink("https://secureonline-qa.pnc.com/wba-ma-rel-rewards-account-summary-ui/main.js", "QA main.js")),
            ImageAttachment("image-2026-4-15_12-4-37.png", height: 400, "build pipeline screenshot"),
            ImageAttachment("image-2026-4-15_12-4-52.png", height: 400, "bundle output screenshot"),
            Heading(level: 2, "Full Configuration Comparison"),
            comparison,
            Heading(level: 2, "Proposed Dockerfile"),
            Code("docker",
                "FROM docker-release.docker.pncint.net/pnc/baseimages/apache-rhel8:2.4-1-0210261529\n" +
                "COPY dist /opt/rh/httpd24/root/var/www/html\nENTRYPOINT [\"/usr/bin/run-httpd\"]"),
            Heading(level: 2, "Verification Steps"),
            TaskList(new[]
            {
                (true, "Run npm run build:micro-app:prod"),
                (false, "Inspect dist/main.js is minified"),
                (false, "Rescan for the vulnerability in PROD"),
            }),
            Panel("warning", "Not a library vulnerability",
                Para("The comments are standard source comments expected to be stripped during minification.")));

        var page = new SeedPage
        {
            Title = "Vulnerability Investigation: Exposing Library Comments - VUF-44661228",
            BodyStorage = body,
            Author = new Author("ssubbiah", "Saravanan Subbiah"),
        };
        page.Labels.AddRange(["harmony", "angular", "vulnerability", "security"]);
        page.Attachments.Add(new SeedAttachment("image-2026-4-15_12-4-37.png", Assets.Png, "image/png", "pipeline screenshot"));
        page.Attachments.Add(new SeedAttachment("image-2026-4-15_12-4-52.png", Assets.Png, "image/png", "bundle screenshot"));
        for (int v = 0; v < 7; v++)
        {
            page.ExtraVersions.Add((body + Para($"Edit pass {v + 1}."), Pick()));
        }

        return page;
    }

    // ---------------------------------------------------------------- Synthetic-flavoured spaces

    private SeedSpace BuildPlatformSre()
    {
        var space = new SeedSpace
        {
            Key = "SRE",
            Name = "Platform SRE",
            Description = "Reliability runbooks, on-call procedures and incident retrospectives.",
        };
        SeedPage home = Home("Platform SRE Home", "On-call, runbooks and incident reviews.",
            ["Showcase: Storage Format Coverage"]);
        home.Children.Add(Showcase(space.Key, "SRE"));
        space.Pages.Add(home);
        return space;
    }

    private SeedSpace BuildDeployPipeline()
    {
        var space = new SeedSpace
        {
            Key = "DDP",
            Name = "Deploy Pipeline",
            Description = "Enterprise CD pipeline, Helm deploy repos and GitOps conventions.",
        };
        SeedPage home = Home("Deploy Pipeline Home", "Helm, GitOps and the enterprise CD pipeline.",
            ["Showcase: Storage Format Coverage"]);
        home.Children.Add(Showcase(space.Key, "Deploy"));
        space.Pages.Add(home);
        return space;
    }

    // ---------------------------------------------------------------- Shared builders

    private static SeedPage Home(string title, string intro, IEnumerable<string> childTitles)
    {
        string body = string.Concat(
            Para(Esc(intro)),
            Toc(),
            Heading(level: 2, "In this space"),
            BulletList(childTitles.Select(t => PageLink(t, t))),
            Para("Questions? Reach the team via " + MailLink("directory-services@pnc.example") + "."));
        var page = new SeedPage { Title = title, BodyStorage = body, Author = Admin };
        page.Labels.AddRange(["reference"]);
        return page;
    }

    // Kitchen-sink: every storage construct in one body + attachments + image.
    private SeedPage Showcase(string spaceKey, string flavour)
    {
        string body = string.Concat(
            Para("This page deliberately exercises " + Bold("every") + " storage-format construct the ingestion " +
                 "pipeline handles. " + Emoticon("smile") + " " + Status("Blue", flavour)),
            Toc(),
            AnchorTarget("top"),
            Heading(level: 2, "Links"),
            BulletList(new[]
            {
                "Relative page link: " + PageLink("CyberArk Links", "CyberArk Links"),
                "Cross-page anchor: " + PageAnchorLink("LDAP Authentication", "policy", "LDAP policy limits"),
                "Same-page anchor: " + SamePageAnchorLink("top", "back to top"),
                "Absolute external: " + ExternalLink("https://confluence.pncint.net/display/CET/CyberArk", "CET wiki"),
                "Root-relative: " + RootRelativeLink($"/spaces/{spaceKey}/overview", "space overview"),
                "Attachment link: " + AttachmentLink("runbook.txt", "runbook.txt"),
                "Mailto: " + MailLink("oncall@pnc.example"),
                "User mention: " + UserMention("placeholder-key"),
            }),
            Heading(level: 2, "Images"),
            Para("Attached image: " + ImageAttachment("diagram.png", height: 120, "architecture diagram")),
            Para("External image: "
                 + ImageExternal("https://confluence.atlassian.com/images/logo/confluence_48_trans.png", width: 48)),
            Heading(level: 2, "Tables"),
            Table(new[] { "Key", "Value" }, new List<IReadOnlyList<string>>
            {
                new[] { "Environment", "PROD" },
                new[] { "Owner", UserMention("placeholder-key") },
            }),
            MergedCellTable(),
            Heading(level: 2, "Lists & tasks"),
            OrderedList(new[] { "First", "Second", "Third" }),
            NestedList("Parent item", new[] { "Child A", "Child B" }),
            TaskList(new[] { (true, "Provision safe"), (false, "Rotate key") }),
            Heading(level: 2, "Code & preformatted"),
            Code("bash", "base64 -w0 inputfile.type > outputfile.b64"),
            NoFormat("CACPM336E reconcilepass password process terminated. Timeout (240) elapsed"),
            HtmlMacro("<p>Raw <strong>HTML</strong> macro body &mdash; re-tokenized by the cleaner.</p>"),
            Heading(level: 2, "Panels & macros"),
            Panel("info", "Info panel", Para("Informational note with an " + InlineCode("inline code") + " span.")),
            Panel("note", title: null, Para("A note without a title.")),
            Panel("warning", "Careful", Para("Warning panel body.")),
            Panel("tip", "Pro tip", Para("Tip panel body.")),
            Expand("Click to expand", Para("Hidden detail revealed on expand. " + Jira("KEY-1"))),
            Para("Inline status: " + Status("Red", "BLOCKED") + " and a Jira link: " + Jira(_faker.PickRandom(s_jiraKeys))),
            Placeholder("Replace this instructional placeholder text"),
            HorizontalRule(),
            Para(Esc("Entities & unicode: café, naïve, 50% & rising, <tag>, em—dash, \U0001f680.")));

        var page = new SeedPage { Title = "Showcase: Storage Format Coverage", BodyStorage = body, Author = Pick() };
        page.Labels.AddRange(["reference", "how-to", spaceKey.ToLowerInvariant()]);
        page.Attachments.Add(new SeedAttachment("diagram.png", Assets.Png, "image/png", "diagram"));
        page.Attachments.Add(new SeedAttachment("runbook.txt", Assets.Text("1. Page on-call\n2. Check dashboards\n3. Mitigate"),
            "text/plain", "runbook"));
        return page;
    }

    private void AddSyntheticContent(SeedSpace space)
    {
        SeedPage home = space.Pages[0];

        // A "Runbooks" section parent so synthetic leaves nest two levels deep (richer ancestor chains).
        var runbooks = new SeedPage
        {
            Title = $"{space.Name} Runbooks",
            BodyStorage = string.Concat(Para("Index of generated runbooks for " + Esc(space.Name) + "."), Toc()),
            Author = Pick(),
        };
        runbooks.Labels.Add("runbook");
        home.Children.Add(runbooks);

        var allTitles = new List<string>();
        for (int i = 0; i < options.Corpus.SyntheticPagesPerSpace; i++)
        {
            SeedPage leaf = SyntheticPage(space, i);
            allTitles.Add(leaf.Title);
            runbooks.Children.Add(leaf);
        }

        // Cross-link some leaves to each other so the graph isn't a pure tree.
        foreach (SeedPage leaf in runbooks.Children)
        {
            string target = _faker.PickRandom(allTitles);
            // (Append a "related" link without rebuilding — handled via ExtraVersions to keep v1 clean.)
            leaf.ExtraVersions.Insert(index: 0, (leaf.BodyStorage + Para("Related: " + PageLink(target, target)), leaf.Author));
        }

        for (int i = 0; i < options.Corpus.BlogPostsPerSpace; i++)
        {
            space.Pages.Add(SyntheticBlog(space, i));
        }
    }

    private SeedPage SyntheticPage(SeedSpace space, int index)
    {
        string verb = _faker.PickRandom("Configuring", "Troubleshooting", "Onboarding", "Rotating", "Migrating", "Hardening");
        string subject = _faker.PickRandom("CyberArk safes", "LDAP bind accounts", "gMSA credentials", "Helm secrets",
            "Kerberos SPNs", "OpenShift secrets", "Conjur API keys", "AAM entries");
        string title = $"{verb} {subject} ({space.Key}-{index + 100})";

        string body = string.Concat(
            Para(_faker.Lorem.Paragraph()),
            Heading(level: 2, "Procedure"),
            OrderedList(Enumerable.Range(start: 1, _faker.Random.Int(min: 3, max: 6)).Select(_ => Esc(_faker.Lorem.Sentence()))),
            _faker.Random.Bool()
                ? Code(_faker.PickRandom("bash", "yaml", "powershell", "groovy"), _faker.Lorem.Sentences(2))
                : Panel(_faker.PickRandom("info", "note", "tip", "warning"), _faker.Lorem.Word(), Para(_faker.Lorem.Sentence())),
            Heading(level: 2, "References"),
            BulletList(new[]
            {
                ExternalLink(_faker.Internet.Url(), _faker.Company.CatchPhrase()),
                PageLink("Showcase: Storage Format Coverage", "Storage format showcase"),
            }),
            Para("Tracking: " + Jira(_faker.PickRandom(s_jiraKeys))));

        var page = new SeedPage { Title = title, BodyStorage = body, Author = Pick() };
        page.Labels.AddRange(PickLabels(min: 1, max: 4));
        if (_faker.Random.Double() < options.Corpus.MultiVersionRatio)
        {
            int edits = _faker.Random.Int(min: 1, max: 3);
            for (int v = 0; v < edits; v++)
            {
                page.ExtraVersions.Add((body + Para($"Edit {v + 1}: " + Esc(_faker.Lorem.Sentence())), Pick()));
            }
        }

        return page;
    }

    private SeedPage SyntheticBlog(SeedSpace space, int index)
    {
        string title = $"{space.Name}: {_faker.Company.CatchPhrase()} ({index + 1})";
        string body = string.Concat(
            Para(_faker.Lorem.Paragraph()),
            Panel("info", "TL;DR", Para(Esc(_faker.Lorem.Sentence()))),
            Para(_faker.Lorem.Paragraph()),
            BulletList(Enumerable.Range(start: 0, count: 3).Select(_ => Esc(_faker.Lorem.Sentence()))));
        var page = new SeedPage { Title = title, Type = "blogpost", BodyStorage = body, Author = Pick() };
        page.Labels.AddRange(PickLabels(min: 1, max: 3));
        return page;
    }
}
