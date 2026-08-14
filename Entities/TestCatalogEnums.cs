namespace SIT.DepartmentSystem.Web.Entities;

public enum TestEnvironmentStatus { Active, Maintenance, Disabled }
public enum BookingMode { Exclusive, Shared }
public enum EquipmentGroupStatus { Active, Disabled }
public enum TestCapabilityStatus { Draft, Active, Retired }
public enum TemplateStatus { Draft, Published, Retired }
public enum ReportTemplateType { Excel, PDF, Other }
public enum AutomationLevel { Manual, SemiAuto, Auto }
public enum TestExecutionProfileStatus { Active, Disabled }
public enum PlanningSource { Agent, Manual }
public enum PlannedTestItemStatus { Draft, WaitingResource, Ready, Running, Completed, Returned, Cancelled }
