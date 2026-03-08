namespace WebApplication1.Helpers
{
    public static class VinDecoder
    {
        private static readonly Dictionary<char, string> CountryMap = new()
        {
            ['1'] = "United States", ['2'] = "Canada", ['3'] = "Mexico",
            ['J'] = "Japan", ['K'] = "South Korea", ['L'] = "China",
            ['S'] = "United Kingdom", ['T'] = "Czech Republic / Hungary", ['V'] = "France / Spain", ['W'] = "Germany",
            ['Y'] = "Sweden / Finland", ['Z'] = "Italy",
            ['9'] = "Brazil", ['6'] = "Australia", ['8'] = "Argentina"
        };

        private static readonly Dictionary<string, string> ManufacturerMap = new()
        {
            ["1G1"] = "Chevrolet", ["1G2"] = "Pontiac", ["1GC"] = "Chevrolet Truck",
            ["1HG"] = "Honda", ["1FA"] = "Ford", ["1FM"] = "Ford SUV",
            ["1FT"] = "Ford Truck", ["1N4"] = "Nissan", ["2HG"] = "Honda (Canada)",
            ["3FA"] = "Ford (Mexico)", ["3MZ"] = "Mazda", ["3VW"] = "Volkswagen (Mexico)",
            ["JM1"] = "Mazda", ["JMZ"] = "Mazda",
            ["JHM"] = "Honda (Japan)", ["JTD"] = "Toyota", ["JTE"] = "Toyota SUV",
            ["JN1"] = "Nissan (Japan)", ["KMH"] = "Hyundai", ["KNA"] = "Kia",
            ["LFV"] = "FAW-Volkswagen", ["SAJ"] = "Jaguar", ["SAL"] = "Land Rover",
            ["SCC"] = "Lotus", ["TMA"] = "Hyundai (Turkey)", ["TMB"] = "Škoda",
            ["TRU"] = "Audi (Hungary)", ["UU1"] = "Renault",
            ["VF1"] = "Renault", ["VF3"] = "Peugeot", ["VF7"] = "Citroën",
            ["VSS"] = "SEAT", ["VWV"] = "Volkswagen (Spain)",
            ["WAU"] = "Audi", ["WBA"] = "BMW", ["WBS"] = "BMW M",
            ["WDB"] = "Mercedes-Benz", ["WDD"] = "Mercedes-Benz",
            ["WF0"] = "Ford (Germany)", ["WMW"] = "MINI", ["WP0"] = "Porsche",
            ["WVW"] = "Volkswagen", ["WV2"] = "Volkswagen Commercial",
            ["XTA"] = "Lada/AvtoVAZ", ["YV1"] = "Volvo",
            ["ZAR"] = "Alfa Romeo", ["ZCF"] = "Iveco", ["ZFA"] = "Fiat",
            ["ZFF"] = "Ferrari", ["ZHW"] = "Lamborghini", ["ZLA"] = "Lancia"
        };

        private static readonly Dictionary<char, string> VehicleTypeMap = new()
        {
            ['1'] = "Car", ['2'] = "SUV / Crossover", ['3'] = "Truck",
            ['4'] = "SUV", ['5'] = "Car", ['6'] = "MPV / Minivan",
            ['7'] = "Truck", ['8'] = "Van", ['9'] = "Utility",
            ['A'] = "Car", ['B'] = "SUV", ['C'] = "Truck",
            ['D'] = "Car", ['E'] = "SUV / Truck", ['F'] = "Van",
            ['G'] = "SUV", ['H'] = "Car", ['J'] = "Car",
            ['K'] = "Truck", ['L'] = "Car", ['M'] = "SUV",
            ['N'] = "Car", ['P'] = "Car", ['R'] = "SUV"
        };

        private static readonly Dictionary<char, string> ModelYearMap = new()
        {
            ['1'] = "2001", ['2'] = "2002", ['3'] = "2003", ['4'] = "2004",
            ['5'] = "2005", ['6'] = "2006", ['7'] = "2007", ['8'] = "2008",
            ['9'] = "2009", ['A'] = "2010", ['B'] = "2011", ['C'] = "2012",
            ['D'] = "2013", ['E'] = "2014", ['F'] = "2015", ['G'] = "2016",
            ['H'] = "2017", ['J'] = "2018", ['K'] = "2019", ['L'] = "2020",
            ['M'] = "2021", ['N'] = "2022", ['P'] = "2023", ['R'] = "2024",
            ['S'] = "2025", ['T'] = "2026", ['V'] = "2027", ['W'] = "2028",
            ['X'] = "2029", ['Y'] = "2030"
        };

        public static object Decode(string? vin)
        {
            if (string.IsNullOrWhiteSpace(vin) || vin.Length < 17)
            {
                return new
                {
                    raw = vin,
                    manufacturer = (string?)null,
                    country = (string?)null,
                    vehicleType = (string?)null,
                    modelYear = (string?)null,
                    plantCode = (string?)null,
                    serialNumber = (string?)null
                };
            }

            var wmi = vin[..3];
            var country = CountryMap.GetValueOrDefault(vin[0], $"Unknown ({vin[0]})");
            var manufacturer = ManufacturerMap.GetValueOrDefault(wmi);

            if (manufacturer == null)
            {
                // Try 2-char prefix match
                manufacturer = ManufacturerMap.FirstOrDefault(kv => kv.Key.StartsWith(vin[..2])).Value
                    ?? ManufacturerMap.FirstOrDefault(kv => wmi.StartsWith(kv.Key[..2])).Value
                    ?? $"Unknown ({wmi})";
            }

            var vehicleDescriptor = vin[3..8]; // VDS section
            var vehicleType = ResolveVehicleType(manufacturer ?? "", vehicleDescriptor, vin[3]);

            // Model year is at position 10 (index 9)
            // For digits 1-9, if position 7 (index 6) is a digit, year is 2001-2009
            // If position 7 is a letter, year is 2031-2039
            var yearChar = vin[9];
            string modelYear;
            if (ModelYearMap.TryGetValue(yearChar, out var baseYear))
            {
                int year = int.Parse(baseYear);
                // Disambiguate: digit years (1-9) could be 200x or 203x
                // Check 7th position: letters suggest newer (2010+), digits suggest older
                if (yearChar >= '1' && yearChar <= '9' && vin[6] >= 'A')
                    year += 30; // 2031-2039
                modelYear = year.ToString();
            }
            else
            {
                modelYear = $"Unknown ({yearChar})";
            }

            var plantCode = vin[10].ToString();
            var serialNumber = vin[11..];

            return new
            {
                raw = vin,
                manufacturer,
                country,
                vehicleType,
                modelYear,
                plantCode,
                serialNumber
            };
        }

        private static string ResolveVehicleType(string manufacturer, string vds, char typeChar)
        {
            // Manufacturer-specific VDS decoding
            if (manufacturer.Contains("Mazda"))
            {
                return vds[0] switch
                {
                    'B' or 'K' => "Sedan (Mazda 3)",
                    'C' => "Hatchback (Mazda 3)",
                    'D' => "Sedan (Mazda 6)",
                    'E' => "SUV (CX-5)",
                    'G' => "SUV (CX-9)",
                    'J' => "SUV (CX-30)",
                    'T' => "Pickup (BT-50)",
                    'N' => "Roadster (MX-5)",
                    _ => $"Mazda ({vds[0]})"
                };
            }
            if (manufacturer.Contains("koda"))
            {
                return vds[0] switch
                {
                    'A' => "Hatchback (Fabia)",
                    'B' => "Sedan/Combi (Octavia)",
                    'C' => "Sedan/Combi (Superb)",
                    'D' => "SUV (Kodiaq)",
                    'E' => "SUV (Karoq)",
                    'F' => "SUV (Kamiq)",
                    'N' => "Hatchback (Scala)",
                    _ => $"Škoda ({vds[0]})"
                };
            }
            if (manufacturer.Contains("BMW"))
            {
                return vds[0] switch
                {
                    'A' or 'B' or 'E' => "Sedan",
                    'C' or 'F' => "Coupe",
                    'G' or 'H' => "Convertible",
                    'X' or 'U' => "SAV / SUV",
                    _ => $"BMW ({vds[0]})"
                };
            }
            if (manufacturer.Contains("Volkswagen"))
            {
                return vds[0] switch
                {
                    'A' or 'B' => "Sedan (Golf/Jetta)",
                    'C' => "Sedan (Passat)",
                    'D' => "SUV (Tiguan)",
                    'T' => "SUV (Touareg)",
                    _ => $"VW ({vds[0]})"
                };
            }

            return VehicleTypeMap.GetValueOrDefault(typeChar, $"Unknown ({typeChar})");
        }
    }
}
