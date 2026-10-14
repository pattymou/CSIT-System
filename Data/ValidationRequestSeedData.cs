using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Entities;

namespace SIT.DepartmentSystem.Web.Data;

internal static class ValidationRequestSeedData
{
    private static readonly DateTime SeededAt = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);

    public static void Configure(ModelBuilder modelBuilder)
    {
        var templateId = Id("template:VALIDATION_TEST_REQUEST");
        var versionId = Id("template:VALIDATION_TEST_REQUEST:v1");
        modelBuilder.Entity<ValidationFormTemplate>().HasData(new ValidationFormTemplate
        {
            Id = templateId, Code = "VALIDATION_TEST_REQUEST", DisplayName = "驗證測試申請單",
            CreatedAt = SeededAt, UpdatedAt = SeededAt
        });
        modelBuilder.Entity<ValidationFormTemplateVersion>().HasData(new ValidationFormTemplateVersion
        {
            Id = versionId, TemplateId = templateId, VersionNumber = 1, Status = ValidationTemplateStatuses.Draft,
            CreatedAt = SeededAt, UpdatedAt = SeededAt
        });

        var productTypes = new[]
        {
            Product("WIFI_AP", "Wi-Fi AP", 10), Product("WIFI_STATION", "Wi-Fi Station", 20),
            Product("FIVE_G_FWA", "5G FWA", 30), Product("FIVE_G_ODU", "5G ODU", 40),
            Product("FIVE_G_DONGLE_WIFI", "5G Dongle w/ Wi-Fi", 50),
            Product("FIVE_G_DONGLE_NO_WIFI", "5G Dongle w/o Wi-Fi", 60),
            Product("SMALLCELL", "SmallCell", 70), Product("O_RU", "O-RU", 80)
        };
        modelBuilder.Entity<ValidationProductType>().HasData(productTypes);

        var sections = new[]
        {
            Section("COMMON_PRODUCT_INFO", "共通產品資訊", 10, true),
            Section("TYPE_WIFI", "Wi-Fi", 20),
            Section("TYPE_CELLULAR_UE", "Cellular UE", 30),
            Section("TYPE_SMALLCELL", "SmallCell", 40)
        };
        modelBuilder.Entity<ValidationSectionDefinition>().HasData(sections);
        var sectionMap = new Dictionary<string, string[]>
        {
            ["WIFI_AP"] = ["TYPE_WIFI"], ["WIFI_STATION"] = ["TYPE_WIFI"],
            ["FIVE_G_FWA"] = ["TYPE_WIFI", "TYPE_CELLULAR_UE"],
            ["FIVE_G_ODU"] = ["TYPE_CELLULAR_UE"],
            ["FIVE_G_DONGLE_WIFI"] = ["TYPE_WIFI", "TYPE_CELLULAR_UE"],
            ["FIVE_G_DONGLE_NO_WIFI"] = ["TYPE_CELLULAR_UE"],
            ["SMALLCELL"] = ["TYPE_SMALLCELL"], ["O_RU"] = ["TYPE_SMALLCELL"]
        };
        modelBuilder.Entity<ValidationProductTypeSectionMapping>().HasData(sectionMap.SelectMany(x => x.Value.Select(s =>
            new ValidationProductTypeSectionMapping { ProductTypeId = Id("product:" + x.Key), SectionDefinitionId = Id("section:" + s) })));

        var fields = new List<ValidationFieldDefinition>();
        var options = new List<ValidationFieldOption>();
        var conditions = new List<ValidationFieldCondition>();
        void Field(string section, string code, string label, string type, int order, bool required = false,
            bool sensitive = false, string? dataSource = null, string? parent = null, string? help = null,
            string? validation = null, string? design = null)
        {
            fields.Add(new ValidationFieldDefinition
            {
                Id = Id("field:" + code), SectionDefinitionId = Id("section:" + section),
                ParentFieldDefinitionId = parent is null ? null : Id("field:" + parent), Code = code,
                DisplayLabel = label, FieldType = type, DisplayOrder = order, IsRequired = required,
                IsEnabled = true, IsSensitive = sensitive, DataSource = dataSource, HelpText = help,
                ValidationJson = validation, DesignNote = design, WasPublished = false
            });
        }
        void Options(string field, params (string Code, string Label)[] values)
        {
            var order = 10;
            foreach (var value in values)
            {
                options.Add(new ValidationFieldOption
                {
                    Id = Id($"option:{field}:{value.Code}"), FieldDefinitionId = Id("field:" + field),
                    ValueCode = value.Code, DisplayLabel = value.Label, DisplayOrder = order, IsEnabled = true
                });
                order += 10;
            }
        }
        void Condition(string target, string source, string op, string? value, string action) => conditions.Add(new ValidationFieldCondition
        {
            Id = Id($"condition:{target}:{source}:{op}:{value}:{action}"), TargetFieldDefinitionId = Id("field:" + target),
            SourceFieldCode = source, Operator = op, ExpectedValue = value, Action = action
        });
        void VisibleRequired(string target, string source, string op, string value)
        {
            Condition(target, source, op, value, "Visible");
            Condition(target, source, op, value, "Required");
        }

        const string common = "COMMON_PRODUCT_INFO";
        Field(common, "LOCATION", "Location", "SingleSelect", 10, true); Options("LOCATION", ("TAIPEI", "Taipei"), ("WUJIANG", "Wujiang"));
        Field(common, "PRODUCT_TYPE", "Product Type", "Dropdown", 20, true, dataSource: "ValidationProductType");
        Field(common, "MODEL_NAME", "Model Name (Askey Internal)", "Text", 30, true);
        Field(common, "PRODUCT_NAME", "Product Name (Customer)", "Text", 40);
        Field(common, "CUSTOMER", "Customer Name / Code", "Lookup", 50, true, dataSource: "SystemOption:Customer");
        Field(common, "NPI_PHASE", "NPI Phase", "Dropdown", 60, true); Options("NPI_PHASE", ("ES", "ES"), ("EV", "EV"), ("DV", "DV"), ("PV", "PV"), ("MP", "MP"), ("OTHER", "其他"));
        Field(common, "PU_CODE", "PU Code", "Dropdown", 70, true); Options("PU_CODE", "1A 1B 1C 2A 2E 2F 2G 2M 3A 4A 5A 6A 6B 6C 9A 9G 9H".Split(' ').Select(x => (x, x)).ToArray());
        Field(common, "DEPARTMENT_CODE", "Department Code", "Text", 80, true);
        Field(common, "HARDWARE_ENGINEER", "Hardware Engineer", "Text", 90, true);
        Field(common, "SOFTWARE_ENGINEER", "Software Engineer", "Text", 100, true);
        Field(common, "PJM", "PJM", "Text", 110, true);
        Field(common, "HW_VERSION", "HW Version", "Text", 120, true);
        Field(common, "FW_SW_VERSION", "FW/SW Version", "Text", 130, true);
        Field(common, "CPU_MODEL", "CPU Model Name", "Text", 140, true);
        Field(common, "ETHERNET_PORTS", "Ethernet / SFP+ Port Number (LAN)", "RepeatableGroup", 150, true, design: "Each interface has a speed/type and Port Count.");
        Field(common, "ETHERNET_INTERFACE", "Interface", "Dropdown", 10, true, parent: "ETHERNET_PORTS"); Options("ETHERNET_INTERFACE", ("1G", "1Gbps"), ("2_5G", "2.5Gbps"), ("5G", "5Gbps"), ("10G", "10Gbps"), ("SFP_1G", "SFP+ 1Gbps"), ("SFP_10G", "SFP+ 10Gbps"));
        Field(common, "ETHERNET_PORT_COUNT", "Port Count", "Number", 20, true, parent: "ETHERNET_PORTS", validation: "{\"integer\":true,\"min\":1}");
        Field(common, "OTHER_PRODUCT_INFO", "上傳其他產品資訊 (PRD/HW Diagram/SPEC/Pre-test Report)", "Attachment", 160, design: "Phase 1 preview only; no file is persisted.");
        Field(common, "DEFAULT_IP_MODE", "Default IP", "SingleSelect", 170, true); Options("DEFAULT_IP_MODE", ("FIXED_IP", "Fixed IP"), ("DHCP_CLIENT", "DHCP Client"));
        Field(common, "DEFAULT_IP_VALUE", "IP", "Text", 180); VisibleRequired("DEFAULT_IP_VALUE", "DEFAULT_IP_MODE", "Equals", "FIXED_IP");
        Field(common, "DEFAULT_USERNAME", "Username", "Text", 190, true);
        Field(common, "DEFAULT_PASSWORD", "Password", "SensitiveText", 200, true, true);
        Field(common, "SSH_TELNET", "SSH/Telnet", "SingleSelect", 210, true); Options("SSH_TELNET", ("ENABLE", "Enable"), ("DISABLE", "Disable"));
        Field(common, "SSH_LOGIN_INFO", "SSH/Telnet Login Info", "Group", 220); VisibleRequired("SSH_LOGIN_INFO", "SSH_TELNET", "Equals", "ENABLE");
        Field(common, "SSH_USERNAME", "Username", "Text", 10, parent: "SSH_LOGIN_INFO");
        Field(common, "SSH_PASSWORD", "Password", "SensitiveText", 20, sensitive: true, parent: "SSH_LOGIN_INFO");

        const string wifi = "TYPE_WIFI";
        Field(wifi, "WIFI_CHIPSET_MAIN", "Wi-Fi Chipset Model Name (Main)", "Text", 10, true);
        Field(wifi, "WIFI_CHIPSET_24", "Wi-Fi Chipset Model Name (2.4GHz)", "Text", 20, true);
        Field(wifi, "WIFI_CHIPSET_5", "Wi-Fi Chipset Model Name (5GHz)", "Text", 30, true);
        Field(wifi, "WIFI_CHIPSET_6", "Wi-Fi Chipset Model Name (6GHz)", "Text", 40, true);
        Field(wifi, "WIFI_DRIVER_VERSION", "Wi-Fi Driver Version", "Text", 50, true);
        Field(wifi, "WIFI_ROLE", "Wi-Fi Role", "Dropdown", 60, true); Options("WIFI_ROLE", ("AP", "AP"), ("STA", "STA"), ("BOTH", "Both"));
        Field(wifi, "WIFI_MODE", "Wi-Fi Mode", "Dropdown", 70, true); Options("WIFI_MODE", Enumerable.Range(4, 5).Select(x => ($"WIFI_{x}", $"Wi-Fi {x}")).ToArray());
        Field(wifi, "WIFI_BAND", "Wi-Fi Band", "MultiSelect", 80, true); Options("WIFI_BAND", ("BAND_24", "2.4 GHz"), ("BAND_5", "5 GHz"), ("BAND_6", "6 GHz"));
        Field(wifi, "MAX_BANDWIDTH", "Max. Bandwidth", "Group", 90);
        Field(wifi, "MAX_BW_24", "2.4 GHz", "MultiSelect", 10, parent: "MAX_BANDWIDTH"); Options("MAX_BW_24", ("20", "20MHz"), ("40", "40MHz")); VisibleRequired("MAX_BW_24", "WIFI_BAND", "Contains", "BAND_24");
        Field(wifi, "MAX_BW_5", "5 GHz", "MultiSelect", 20, parent: "MAX_BANDWIDTH"); Options("MAX_BW_5", ("80", "80MHz"), ("160", "160MHz")); VisibleRequired("MAX_BW_5", "WIFI_BAND", "Contains", "BAND_5");
        Field(wifi, "MAX_BW_6", "6 GHz", "MultiSelect", 30, parent: "MAX_BANDWIDTH"); Options("MAX_BW_6", ("160", "160MHz"), ("320", "320MHz")); VisibleRequired("MAX_BW_6", "WIFI_BAND", "Contains", "BAND_6");
        Field(wifi, "SPATIAL_STREAM", "Spatial Stream", "Group", 100);
        foreach (var band in new[] { ("24", "2.4 GHz", "BAND_24", 10), ("5", "5 GHz", "BAND_5", 20), ("6", "6 GHz", "BAND_6", 30) })
        { var code = "SPATIAL_STREAM_" + band.Item1; Field(wifi, code, band.Item2, "Dropdown", band.Item4, parent: "SPATIAL_STREAM"); Options(code, new[] { "1x1", "2x2", "3x3", "4x4", "5x5", "8x8" }.Select(x => (x.ToUpperInvariant(), x)).ToArray()); VisibleRequired(code, "WIFI_BAND", "Contains", band.Item3); }
        Field(wifi, "CHANNEL_24", "Supported Channel 2.4GHz", "SingleSelect", 110); Options("CHANNEL_24", ("CH1_11", "CH1-11"), ("CH1_13", "CH1-13")); VisibleRequired("CHANNEL_24", "WIFI_BAND", "Contains", "BAND_24");
        Field(wifi, "CHANNEL_5", "Supported Channel 5GHz", "MultiSelect", 120); Options("CHANNEL_5", new[] { "CH36-48", "CH52-64", "CH100-112", "CH116-128", "CH132-144", "CH149-161", "CH165" }.Select(x => (x.Replace('-', '_'), x)).ToArray()); VisibleRequired("CHANNEL_5", "WIFI_BAND", "Contains", "BAND_5");
        Field(wifi, "CHANNEL_6", "Supported Channel 6GHz", "Group", 130); VisibleRequired("CHANNEL_6", "WIFI_BAND", "Contains", "BAND_6");
        Field(wifi, "CHANNEL_6_FROM", "From CH", "Number", 10, parent: "CHANNEL_6", validation: "{\"integer\":true}");
        Field(wifi, "CHANNEL_6_TO", "To CH", "Number", 20, parent: "CHANNEL_6", validation: "{\"integer\":true}");

        const string cellular = "TYPE_CELLULAR_UE";
        Field(cellular, "CELLULAR_CHIPSET", "LTE/5G Chipset Model Name", "Text", 10, true);
        Field(cellular, "CELLULAR_MODEM_DRIVER", "LTE/5G Modem Driver Version", "Text", 20, true);
        Field(cellular, "THREE_GPP_RELEASE", "3GPP Release", "Dropdown", 30); Options("THREE_GPP_RELEASE", new[] { "Rel-19", "Rel-18", "Rel-17", "Rel-16", "Rel-15", "Other" }.Select(x => (x.Replace('-', '_').ToUpperInvariant(), x)).ToArray());
        Field(cellular, "SUPPORTED_RATS", "Supported RATs", "MultiSelect", 40, true); Options("SUPPORTED_RATS", ("LTE", "4G LTE"), ("NR_SA", "5G NR SA"), ("EN_DC", "EN-DC"), ("REDCAP", "RedCap"), ("EREDCAP", "eRedCap"));
        Field(cellular, "NR_BANDS", "5G NR Bands", "Text", 50, true);
        Field(cellular, "LTE_BANDS", "4G LTE Bands", "Text", 60, true);
        Field(cellular, "CA_DC_COMBINATIONS", "CA/DC Band Combinations", "Attachment", 70);
        Field(cellular, "FR2_SUPPORTED", "Frequency Range 2 (FR2)", "Checkbox", 80);
        Field(cellular, "FR2_BANDS", "FR2 Bands", "Text", 90); Condition("FR2_BANDS", "FR2_SUPPORTED", "IsTrue", null, "Visible");
        Field(cellular, "APN_DATA", "APN - Data", "RepeatableText", 100);
        Field(cellular, "AT_COMMAND", "AT Command", "TextArea", 110, true, help: "Instructions to access AT command");
        Field(cellular, "ESIM_SUPPORT", "eSIM Support (if applicable)", "TextArea", 120);

        const string small = "TYPE_SMALLCELL";
        Field(small, "SMALLCELL_CHIPSET", "LTE/5G Chipset Model Name", "Text", 10, true);
        Field(small, "SMALLCELL_MODEM_DRIVER", "LTE/5G Modem Driver Version", "Text", 20, true);
        Field(small, "DUPLEX", "Duplex／TDD 配置能力", "MultiSelect", 30); Options("DUPLEX", ("FDD", "FDD"), ("TDD", "TDD"));
        Field(small, "CELL_IDENTITY", "Cell identity 能力", "Text", 40, help: "PCI、Cell ID、TAC、PLMN 等可設定範圍。");
        Field(small, "RF_POWER_ANTENNA", "RF power／天線配置", "Group", 50, true);
        Field(small, "TX_POWER_MAX", "Tx Power Max", "Number", 10, true, parent: "RF_POWER_ANTENNA", validation: "{\"integer\":true,\"unit\":\"dB\"}");
        Field(small, "TX_POWER_MIN", "Tx Power Min", "Number", 20, true, parent: "RF_POWER_ANTENNA", validation: "{\"integer\":true,\"unit\":\"dB\"}");
        Field(small, "RF_ANTENNA_CONFIG", "RF 天線配置", "Group", 60);
        Field(small, "RF_MIMO", "MIMO", "SingleSelect", 10, parent: "RF_ANTENNA_CONFIG"); Options("RF_MIMO", ("2X2", "2x2"), ("4X4", "4x4"));
        Field(small, "RF_BUILTIN", "Build-in RF Antenna", "SingleSelect", 20, parent: "RF_ANTENNA_CONFIG"); Options("RF_BUILTIN", ("YES", "Yes"), ("NO", "No"));
        Field(small, "GPS_ANTENNA", "GPS Antenna", "SingleSelect", 70); Options("GPS_ANTENNA", ("BUILTIN", "Build-in"), ("EXTERNAL", "External"));
        Field(small, "POWER_SUPPLY", "電源供應", "Group", 80);
        Field(small, "POWER_INPUT", "Power Input", "MultiSelect", 10, parent: "POWER_SUPPLY"); Options("POWER_INPUT", ("AC", "AC"), ("DC", "DC"));
        Field(small, "POE_PLUS", "PoE+", "SingleSelect", 20, parent: "POWER_SUPPLY"); Options("POE_PLUS", ("YES", "Yes"), ("NO", "No"));
        Field(small, "POWER_CONSUMPTION", "Power Consumption", "SingleSelect", 90); Options("POWER_CONSUMPTION", ("250MW", "250mW"), ("5W", "5W"));
        Field(small, "SYNC_SOURCE", "同步來源", "MultiSelect", 100); Options("SYNC_SOURCE", ("GPS", "GPS"), ("PTP", "PTP"));
        Field(small, "MANAGEMENT_PORT", "管理埠 Ethernet", "SingleSelect", 110); Options("MANAGEMENT_PORT", ("YES", "Yes"), ("NO", "No"));
        Field(small, "LED_DISPLAY", "LED 顯示", "SingleSelect", 120); Options("LED_DISPLAY", ("ONE", "1顆"), ("FOUR", "4顆"));
        Field(small, "BACKHAUL", "Backhaul Interface / Speed", "Group", 130);
        Field(small, "BACKHAUL_ETHERNET", "Ethernet", "MultiSelect", 10, parent: "BACKHAUL"); Options("BACKHAUL_ETHERNET", ("1G", "1Gbps"), ("2_5G", "2.5Gbps"), ("10G", "10Gbps"));
        Field(small, "BACKHAUL_FIBER", "Fiber", "MultiSelect", 20, parent: "BACKHAUL"); Options("BACKHAUL_FIBER", ("1G", "1Gbps"), ("10G", "10Gbps"));
        Field(small, "MAXIMUM_UE", "Maximum UE", "Dropdown", 140); Options("MAXIMUM_UE", new[] { "32", "64", "128", "256" }.Select(x => (x, x)).ToArray());
        Field(small, "DEPLOYMENT", "部署型態", "MultiSelect", 150); Options("DEPLOYMENT", ("INDOOR", "Indoor"), ("OUTDOOR", "Outdoor"));

        modelBuilder.Entity<ValidationFieldDefinition>().HasData(fields);
        modelBuilder.Entity<ValidationFieldOption>().HasData(options);
        modelBuilder.Entity<ValidationFieldCondition>().HasData(conditions);

        var categories = new[]
        {
            Category("A", "Sample Release Test", 10, true), Category("B", "Wi-Fi", 20),
            Category("C", "Cellular UE", 30), Category("D", "SmallCell", 40)
        };
        modelBuilder.Entity<ValidationTestItemCategory>().HasData(categories);
        var categoryMap = new Dictionary<string, string[]>
        {
            ["WIFI_AP"] = ["B"], ["WIFI_STATION"] = ["B"], ["FIVE_G_FWA"] = ["B", "C"],
            ["FIVE_G_ODU"] = ["C"], ["FIVE_G_DONGLE_WIFI"] = ["B", "C"],
            ["FIVE_G_DONGLE_NO_WIFI"] = ["C"], ["SMALLCELL"] = ["D"], ["O_RU"] = ["D"]
        };
        modelBuilder.Entity<ValidationProductTypeTestCategoryMapping>().HasData(categoryMap.SelectMany(x => x.Value.Select(c =>
            new ValidationProductTypeTestCategoryMapping { ProductTypeId = Id("product:" + x.Key), TestItemCategoryId = Id("category:" + c) })));

        var testItems = new List<ValidationTestItemDefinition>();
        void Item(string category, string code, string label, int order, bool mandatory = false, string? note = null) => testItems.Add(new ValidationTestItemDefinition
        {
            Id = Id("item:" + code), TestItemCategoryId = Id("category:" + category), Code = code,
            DisplayLabel = label, DisplayOrder = order, IsMandatory = mandatory, IsEnabled = true,
            DesignNote = note, WasPublished = false
        });
        var mandatoryNote = "Mandatory test item for each NPI phase";
        Item("B", "WIFI_TRP_TIS", "Wi-Fi TRP/TIS Test", 10, true, mandatoryNote);
        Item("B", "WIFI_OTA_THROUGHPUT", "Wi-Fi OTA Throughput Test", 20, true, mandatoryNote);
        Item("B", "WIFI_RVR", "Wi-Fi RvR Test", 30, true, mandatoryNote);
        Item("B", "WIFI_ANGLE", "Wi-Fi Angle Test", 40); Item("B", "WIFI_COVERAGE", "Wi-Fi Coverage Test", 50); Item("B", "WIFI_INDOOR", "Wi- Fi Indoor Test", 60);
        Item("C", "CELLULAR_TRP_TIS", "LTE/5G TRP/TIS Test", 10, true); Item("C", "CELLULAR_OTA_THROUGHPUT", "LTE/5G OTA Throughput Test", 20, true);
        Item("C", "CELLULAR_UE_CAPABILITY", "LTE/5G UE Capability Test", 30); Item("C", "CELLULAR_BAND_SELECTION", "LTE/5G Band Selection Test", 40); Item("C", "CELLULAR_ENDURANCE", "LTE/5G Endurance Test", 50);
        Item("D", "SC_OTA_THROUGHPUT", "OTA Throughput Test", 10); Item("D", "SC_RATE_VS_RANGE", "Rate vs Range Test", 20);
        Item("D", "SC_AMP_INTEROPERABILITY", "AMP Interoperibility Test", 30); Item("D", "SC_THERMAL", "Thermal Test", 40);
        Item("D", "SC_HANDOVER", "Handover Test", 50); Item("D", "SC_MULTIPLE_UE", "Multiple UE Test", 60);
        Item("D", "SC_INDOOR_COVERAGE", "Indoor Coverage Test", 70); Item("D", "SC_OUTDOOR_COVERAGE", "Outdoor Coverage Test", 80);
        Item("D", "SC_STABILITY", "Stability Test", 90); Item("D", "SC_FUNCTIONAL", "Functional Test", 100);
        modelBuilder.Entity<ValidationTestItemDefinition>().HasData(testItems);

        var parameters = new List<ValidationTestItemParameterDefinition>();
        var parameterOptions = new List<ValidationTestItemParameterOption>();
        void Parameter(string item, string code, string label, int order, params (string Code, string Label)[] values)
        {
            parameters.Add(new ValidationTestItemParameterDefinition
            {
                Id = Id($"parameter:{item}:{code}"), TestItemDefinitionId = Id("item:" + item), Code = code,
                DisplayLabel = label, FieldType = values.Length == 0 ? "Text" : "SingleSelect", DisplayOrder = order,
                IsEnabled = true, DesignNote = "Excel 'maximum groups' is retained as unspecified; no limit is inferred."
            });
            var optionOrder = 10;
            foreach (var value in values)
            {
                parameterOptions.Add(new ValidationTestItemParameterOption
                {
                    Id = Id($"parameter-option:{item}:{code}:{value.Code}"), ParameterDefinitionId = Id($"parameter:{item}:{code}"),
                    ValueCode = value.Code, DisplayLabel = value.Label, DisplayOrder = optionOrder, IsEnabled = true
                });
                optionOrder += 10;
            }
        }
        var defaultSpecific = new[] { ("DEFAULT", "Default"), ("SPECIFIC", "Specific") };
        var antenna = new[] { ("BUILTIN", "Built-in"), ("EXTERNAL_DIRECTIONAL", "External directional Antenna"), ("EXTERNAL_OMNI", "External omnidirectional Antenna") };
        foreach (var item in new[] { "SC_OTA_THROUGHPUT", "SC_RATE_VS_RANGE", "SC_THERMAL", "SC_HANDOVER", "SC_MULTIPLE_UE" })
        {
            Parameter(item, "FREQUENCY", "Frequency / Bandwidth", 10, defaultSpecific);
            Parameter(item, "TDD", item is "SC_RATE_VS_RANGE" or "SC_THERMAL" ? "TDD (Default DDDSU / Specific)" : "TDD Time Slot", 20, defaultSpecific);
            if (item is "SC_OTA_THROUGHPUT" or "SC_RATE_VS_RANGE" or "SC_THERMAL") Parameter(item, "RF_ANTENNA", "RF Antenna", 30, antenna);
        }
        foreach (var item in new[] { "SC_INDOOR_COVERAGE", "SC_OUTDOOR_COVERAGE" })
        { Parameter(item, "FREQUENCY", "Frequency", 10); Parameter(item, "TDD", "TDD", 20); Parameter(item, "RF_ANTENNA", "RF Antenna", 30); }
        Parameter("SC_STABILITY", "FREQUENCY", "Frequency", 10); Parameter("SC_STABILITY", "TDD", "TDD", 20);
        modelBuilder.Entity<ValidationTestItemParameterDefinition>().HasData(parameters);
        modelBuilder.Entity<ValidationTestItemParameterOption>().HasData(parameterOptions);
    }

    private static ValidationProductType Product(string code, string label, int order) => new()
    { Id = Id("product:" + code), Code = code, DisplayLabel = label, DisplayOrder = order, IsEnabled = true, CreatedAt = SeededAt, UpdatedAt = SeededAt };
    private static ValidationSectionDefinition Section(string code, string label, int order, bool always = false) => new()
    { Id = Id("section:" + code), TemplateVersionId = Id("template:VALIDATION_TEST_REQUEST:v1"), Code = code, DisplayLabel = label, DisplayOrder = order, AlwaysVisible = always, IsEnabled = true };
    private static ValidationTestItemCategory Category(string code, string label, int order, bool always = false) => new()
    { Id = Id("category:" + code), TemplateVersionId = Id("template:VALIDATION_TEST_REQUEST:v1"), Code = code, DisplayLabel = label, DisplayOrder = order, AlwaysVisible = always, IsEnabled = true };
    private static Guid Id(string value) => new(MD5.HashData(Encoding.UTF8.GetBytes("validation-request:v1:" + value)));
}
