using Serilog;

namespace MyriadMusicTagger.Utils
{
    /// <summary>
    /// Utility class for parsing cart numbers from various file formats
    /// </summary>
    public static class CsvParser
    {
        /// <summary>
        /// Parses cart numbers from a CSV or text file
        /// </summary>
        /// <param name="filePath">Path to the file containing cart numbers</param>
        /// <returns>List of unique cart numbers sorted in ascending order</returns>
        /// <exception cref="ArgumentException">Thrown when file path is null or empty</exception>
        /// <exception cref="FileNotFoundException">Thrown when file doesn't exist</exception>
        public static List<int> ParseCartNumbersFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
                
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            var cartNumbers = new List<int>();
            var content = File.ReadAllText(filePath);
            
            // Split by various delimiters: comma, semicolon, newline, tab
            var separators = new char[] { ',', ';', '\n', '\r', '\t' };
            var parts = content.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                
                if (int.TryParse(trimmed, out int cartNumber) && cartNumber > 0)
                {
                    if (!cartNumbers.Contains(cartNumber)) // Avoid duplicates
                    {
                        cartNumbers.Add(cartNumber);
                    }
                }
                else
                {
                    Log.Warning("Invalid cart number found in CSV file: {InvalidValue}", trimmed);
                }
            }
            
            return cartNumbers.OrderBy(x => x).ToList(); // Sort for better organization
        }
    }
}
