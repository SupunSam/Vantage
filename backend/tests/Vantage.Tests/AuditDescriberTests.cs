using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>The one-line descriptions shown in the Audit Log.</summary>
public class AuditDescriberTests
{
    [Fact]
    public void Members_added_names_the_group_dashboard_and_ticket()
    {
        var text = AuditDescriber.Describe("group.members-added", "DashboardGroup", "7",
            """{"group":"SALES-North","count":5,"moved":1,"emails":["a@rrd.com","b@rrd.com","c@rrd.com","d@rrd.com","e@rrd.com"]}""", "SALES-North", "Sales Overview", "INC0042");
        Assert.Equal("Added 5 people to group SALES-North on Sales Overview (1 moved from another group): a@rrd.com, b@rrd.com, c@rrd.com and others. Ticket INC0042.", text);
    }

    [Fact]
    public void Job_runs_pauses_and_inactivity_flags_read_as_sentences()
    {
        Assert.Equal("Ran HRMS Sync: Checked 12.", AuditDescriber.Describe("job.run", "JobRun", "3", """{"job":"hrms-sync","title":"HRMS Sync","trigger":"Manual","status":"Succeeded","summary":"Checked 12."}""", null, null, null));
        Assert.Equal("Scheduled run of Inactivity Check: Flagged 2 dashboards.", AuditDescriber.Describe("job.run", "JobRun", "4", """{"job":"inactivity-check","title":"Inactivity Check","trigger":"Schedule","summary":"Flagged 2 dashboards."}""", null, null, null));
        Assert.Equal("Paused the scheduled job New-Hire Digest.", AuditDescriber.Describe("job.paused", "JobState", "new-hire-digest", """{"job":"new-hire-digest","title":"New-Hire Digest"}""", null, null, null));
        Assert.Equal("Flagged Sales Overview as unused: not opened for 120 days.", AuditDescriber.Describe("dashboard.inactivity-flagged", "Dashboard", "1", """{"daysQuiet":120}""", null, "Sales Overview", null));
        Assert.Equal("Ended the access of a@rrd.com after they left: 2 group memberships ended, 1 kept because they own the dashboard.",
            AuditDescriber.Describe("user.access-revoked", "User", "9", """{"email":"a@rrd.com","memberships":2,"keptAsOwner":1}""", null, null, null));
    }

    [Fact]
    public void A_genai_replace_and_restore_read_as_page_changes()
    {
        Assert.Equal("Replaced the page of Sales Page with version 4.", AuditDescriber.Describe("dashboard.replaced", "Dashboard", "1", """{"version":4,"type":"GenAi"}""", null, "Sales Page", null));
        Assert.Equal("Restored version 1 of Sales Page; it is live again as version 5.", AuditDescriber.Describe("dashboard.restored", "Dashboard", "1", """{"fromVersion":1,"version":5,"type":"GenAi"}""", null, "Sales Page", null));
    }

    [Fact]
    public void Role_change_shows_before_and_after()
    {
        var text = AuditDescriber.Describe("user.updated", "User", "3", """{"rolesBefore":["Dashboard User"],"rolesAfter":["Dashboard User","Dashboard Owner"]}""", "priya@rrd.com", null, null);
        Assert.Equal("Changed roles of priya@rrd.com: Dashboard User → Dashboard User, Dashboard Owner.", text);
    }

    [Fact]
    public void Unknown_action_falls_back_to_a_readable_sentence_and_bad_json_is_ignored()
    {
        Assert.Equal("Something happened: Sales Overview.", AuditDescriber.Describe("dashboard.something-happened", "Dashboard", "1", "not json", null, "Sales Overview", null));
    }

    [Fact]
    public void Failed_tenant_check_lists_the_failed_checks()
    {
        var text = AuditDescriber.Describe("tenant.verified", "BiTenant", "2", """{"Passed":false,"failed":["Workspace access"]}""", "Prod PBI", null, null);
        Assert.Equal("Verified tenant Prod PBI: some checks failed (Workspace access).", text);
    }

    [Fact]
    public void Approval_requests_and_overrides_are_described_with_their_reason()
    {
        Assert.Equal("Asked the owners to approve adding 10 people to group SALES-North on Sales Overview: a@rrd.com, b@rrd.com, c@rrd.com and others.",
            AuditDescriber.Describe("group.add-requested", "DashboardGroup", "7", """{"group":"SALES-North","count":10,"emails":["a@rrd.com","b@rrd.com","c@rrd.com","d@rrd.com"]}""", "SALES-North", "Sales Overview", null));
        Assert.Equal("The owners approved adding 8 of 10 people to group SALES-North on Sales Overview, rejected 2.",
            AuditDescriber.Describe("group.add-approved", "DashboardGroup", "7", """{"group":"SALES-North","approved":8,"rejected":2,"total":10}""", null, "Sales Overview", null));
        Assert.Equal("A Super Admin approved in place of the owners adding 1 of 1 person to group G on D. Reason: Owner on leave.",
            AuditDescriber.Describe("group.add-approved", "DashboardGroup", "7", """{"group":"G","approved":1,"rejected":0,"total":1,"overrideReason":"Owner on leave"}""", null, "D", null));
        Assert.Contains("without the owners' approval, reason: Outage",
            AuditDescriber.Describe("group.members-added", "DashboardGroup", "7", """{"group":"G","count":1,"emails":["a@rrd.com"],"overrideReason":"Outage"}""", null, "D", null));
    }
}
