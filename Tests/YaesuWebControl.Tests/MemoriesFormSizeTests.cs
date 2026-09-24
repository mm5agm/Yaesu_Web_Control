using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using Yaesu_Web_Control;

namespace YaesuWebControl.Tests
{
    /// <summary>
    /// The Memories page posts every memory row in a single form, so the
    /// framework's default cap of 1024 form fields is really a cap on how many
    /// memories an operator may own. Cross it and Save returns a bare HTTP 400
    /// with no page and no message -- the antiforgery filter reads the form
    /// first, so even the log blames the antiforgery token rather than the
    /// size. Bruce VK2RT hit it on #167 after importing 99 channels from his
    /// FTdx101D.
    ///
    /// Nothing about that failure is visible in review: the markup looks fine,
    /// every test passes, and it only bites operators with a full radio. These
    /// tests keep the ceiling and the markup honest about each other.
    /// </summary>
    public class MemoriesFormSizeTests
    {
        /// <summary>The framework default, which is what we are raising.</summary>
        private const int AspNetCoreDefaultValueCountLimit = 1024;

        [Fact]
        public void MemoryRowPostsNoMoreFieldsThanTheLimitAssumes()
        {
            string markup = File.ReadAllText(LocateRepoPath("Pages/Memories.cshtml"));
            int fieldsPerRow = Regex.Matches(markup, @"name=""Memories\[@i\]\.").Count;

            Assert.True(fieldsPerRow > 0,
                "Found no per-row fields in Pages/Memories.cshtml. If the page stopped " +
                "posting rows as a form, this whole limit no longer applies and these " +
                "tests should go -- but check, don't just delete them.");

            Assert.True(
                fieldsPerRow <= WebFormLimits.MemoryRowFields,
                $"Pages/Memories.cshtml now posts {fieldsPerRow} fields per memory, but " +
                $"WebFormLimits.MemoryRowFields still says {WebFormLimits.MemoryRowFields}. " +
                "Every extra column lowers the number of memories that can be saved before " +
                "the operator gets an unexplained HTTP 400 (#167). Update the constant.");
        }

        [Fact]
        public void LimitCoversAFullRadioAndThenSome()
        {
            int needed = (WebFormLimits.MemoryRowFields * WebFormLimits.SupportedMemoryRows) + 1;

            Assert.True(WebFormLimits.ValueCountLimit >= needed,
                $"ValueCountLimit {WebFormLimits.ValueCountLimit} cannot carry " +
                $"{WebFormLimits.SupportedMemoryRows} memories plus the antiforgery token.");

            Assert.True(WebFormLimits.SupportedMemoryRows >= 99,
                "A radio holds 99 memory channels and the page's Import reads all of them, " +
                "so anything below 99 ships a Save button that a full import breaks.");

            Assert.True(WebFormLimits.ValueCountLimit > AspNetCoreDefaultValueCountLimit,
                "The whole point is to raise the default. A value at or below it is a no-op.");
        }

        /// <summary>
        /// Exceeding MaxModelBindingCollectionSize throws rather than binding a
        /// truncated list, so the operator gets a bare 500 instead of a bare
        /// 400. Keeping it above the rows the form limit admits means the form
        /// limit is always what stops an absurd post.
        /// </summary>
        [Fact]
        public void ModelBindingCapIsNeverTheFirstToFail()
        {
            int rowsTheFormLimitAdmits = WebFormLimits.ValueCountLimit / WebFormLimits.MemoryRowFields;

            Assert.True(
                WebFormLimits.ModelBindingCollectionSize > rowsTheFormLimitAdmits,
                $"ModelBindingCollectionSize {WebFormLimits.ModelBindingCollectionSize} is below the " +
                $"{rowsTheFormLimitAdmits} rows ValueCountLimit allows through, so a big post would " +
                "throw during binding and show a bare HTTP 500.");
        }

        /// <summary>
        /// A constant nothing reads is worse than no constant: the tests above
        /// would still pass while the app kept the framework default.
        /// </summary>
        [Fact]
        public void StartupActuallyAppliesTheLimits()
        {
            string program = File.ReadAllText(LocateRepoPath("Program.cs"));

            Assert.Contains("WebFormLimits.ValueCountLimit", program, StringComparison.Ordinal);
            Assert.Contains("WebFormLimits.ModelBindingCollectionSize", program, StringComparison.Ordinal);
        }

        private static string LocateRepoPath(string relative)
        {
            string native = relative.Replace('/', Path.DirectorySeparatorChar);
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new FileNotFoundException(
                $"Could not find {relative} above {AppContext.BaseDirectory}. This test reads " +
                "the app's own source and needs the repository tree.");
        }
    }
}
