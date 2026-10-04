import { RulesPanel } from "./RulesPanel";

/**
 * Access Group Rules: "when an HRMS field equals a value, propose adding the person to this group" (or removing them).
 * Super Admins write the rules. A rule never changes anyone's access: what it finds goes to the dashboard's owners as a
 * request, and only their approval adds or removes people. Each group's own rules are also on its page, under Rules.
 */
export function AccessRulesPage() {
  return (
    <>
      <div className="page-head">
        <div>
          <h1>Access Group Rules</h1>
          <p>
            A rule picks people from the HRMS data, for example <em>Department equals Finance</em>, and proposes adding them to an access group, or removing them from it.
            Rules never change anyone's access by themselves: what a rule finds goes to the dashboard's owners as one request, and people are added or removed only when the owners approve.
            Rules run when you run them, and after every HRMS sync. The rules of one group are also on that group's page, under Rules.
          </p>
        </div>
      </div>
      <RulesPanel />
    </>
  );
}
