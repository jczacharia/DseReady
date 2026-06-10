// Copyright (c) PNC Financial Services. All rights reserved.


using Elastic.Mapping;
using Elastic.Mapping.Analysis;
using Elastic.Mapping.Mappings;
using Elastic.Mapping.Mappings.Builders;

namespace Dse.Sources;

public abstract class SourceDocOptions<TDocument> : IConfigureElasticsearch<TDocument> where TDocument : class
{
    public AnalysisBuilder ConfigureAnalysis(AnalysisBuilder analysis) => analysis.DseAnalysis();

    public abstract MappingsBuilder<TDocument> ConfigureMappings(MappingsBuilder<TDocument> mappings);

    public IReadOnlyDictionary<string, string> IndexSettings =>
        ConfigureIndexSettings(new Dictionary<string, string> { ["index.highlight.max_analyzed_offset"] = "10000000" })
            .AsReadOnly();

    protected virtual IDictionary<string, string> ConfigureIndexSettings(Dictionary<string, string> settings) => settings;
}

public static class SourceDocOptionsExtensions
{
    private const string EnStemFilter = "en_stem_filter";
    private const string EnStopWordsFilter = "en_stop_words_filter";
    private const string DelimiterFilter = "delimiter";

    public static AnalysisBuilder DseAnalysis(this AnalysisBuilder analysis) => analysis
        .TokenFilter("front_ngram", f => f.EdgeNGram().MinGram(1).MaxGram(12))
        .TokenFilter("bigram_joiner", f => f.Shingle().MaxShingleSize(2).TokenSeparator("").OutputUnigrams(false))
        .TokenFilter("bigram_joiner_unigrams", f => f.Shingle().MaxShingleSize(2).TokenSeparator("").OutputUnigrams(true))
        .TokenFilter("bigram_max_size", f => f.Length().Min(0).Max(16))
        .TokenFilter(EnStemFilter, f => f.Stemmer().Language("light_english"))
        .TokenFilter(EnStopWordsFilter, f => f.Stop().Stopwords("_english_"))
        .TokenFilter(DelimiterFilter, f => f.WordDelimiterGraph()
            .GenerateWordParts(true)
            .GenerateNumberParts(true)
            .CatenateWords()
            .CatenateNumbers()
            .CatenateAll()
            .SplitOnCaseChange(true)
            .SplitOnNumerics(true)
            .StemEnglishPossessive(true)
            .PreserveOriginal(false))
        .Analyzer("iq_text_base", a => a.Custom()
            .Tokenizer(BuiltInAnalysis.Tokenizers.Standard)
            .Filters(BuiltInAnalysis.TokenFilters.Lowercase, BuiltInAnalysis.TokenFilters.AsciiFolding, EnStopWordsFilter))
        .Analyzer("iq_text_stem", a => a.Custom()
            .Tokenizer(BuiltInAnalysis.Tokenizers.Standard)
            .Filters(BuiltInAnalysis.TokenFilters.Lowercase, BuiltInAnalysis.TokenFilters.AsciiFolding, EnStopWordsFilter,
                EnStemFilter))
        .Analyzer("iq_text_delimiter", a => a.Custom()
            .Tokenizer(BuiltInAnalysis.Tokenizers.Whitespace)
            .Filters(DelimiterFilter, BuiltInAnalysis.TokenFilters.Lowercase, BuiltInAnalysis.TokenFilters.AsciiFolding,
                EnStopWordsFilter, EnStemFilter))
        .Analyzer("iq_text_exact", a => a.Custom()
            .Tokenizer(BuiltInAnalysis.Tokenizers.Standard)
            .Filters(BuiltInAnalysis.TokenFilters.Lowercase, BuiltInAnalysis.TokenFilters.AsciiFolding))
        .Analyzer("i_prefix", a => a.Custom()
            .Tokenizer(BuiltInAnalysis.Tokenizers.Standard)
            .Filters(BuiltInAnalysis.TokenFilters.Lowercase, BuiltInAnalysis.TokenFilters.AsciiFolding, "front_ngram"))
        .Analyzer("q_prefix", a => a.Custom()
            .Tokenizer(BuiltInAnalysis.Tokenizers.Standard)
            .Filters(BuiltInAnalysis.TokenFilters.Lowercase, BuiltInAnalysis.TokenFilters.AsciiFolding))
        .Analyzer("i_text_bigram", a => a.Custom()
            .Tokenizer(BuiltInAnalysis.Tokenizers.Standard)
            .Filters(BuiltInAnalysis.TokenFilters.Lowercase, BuiltInAnalysis.TokenFilters.AsciiFolding, EnStemFilter,
                "bigram_joiner", "bigram_max_size"))
        .Analyzer("q_text_bigram", a => a.Custom()
            .Tokenizer(BuiltInAnalysis.Tokenizers.Standard)
            .Filters(BuiltInAnalysis.TokenFilters.Lowercase, BuiltInAnalysis.TokenFilters.AsciiFolding, EnStemFilter,
                "bigram_joiner_unigrams", "bigram_max_size"))
        .Analyzer("html_english", a => a.Custom()
            .CharFilter(BuiltInAnalysis.CharFilters.HtmlStrip)
            .Tokenizer(BuiltInAnalysis.Tokenizers.Standard)
            .Filters(BuiltInAnalysis.TokenFilters.Lowercase, BuiltInAnalysis.TokenFilters.AsciiFolding, EnStopWordsFilter,
                EnStemFilter));

    public static TextFieldBuilder DseText(this TextFieldBuilder b) => b
        .MultiField("stem", mf => mf.Text().Analyzer("iq_text_stem"))
        .MultiField("delimiter", mf => mf.Text().Analyzer("iq_text_delimiter"))
        .MultiField("exact", mf => mf.Text().Analyzer("iq_text_exact"))
        .MultiField("prefix", mf => mf.Text().Analyzer("i_prefix").SearchAnalyzer("q_prefix"))
        .MultiField("joined", mf => mf.Text().Analyzer("i_text_bigram").SearchAnalyzer("q_text_bigram"))
        .MultiField("regex", mf => mf.Wildcard());

    public static KeywordFieldBuilder DseKeyword(this KeywordFieldBuilder b) => b
        .MultiField("stem", mf => mf.Text().Analyzer("iq_text_stem"))
        .MultiField("delimiter", mf => mf.Text().Analyzer("iq_text_delimiter"))
        .MultiField("exact", mf => mf.Text().Analyzer("iq_text_exact"))
        .MultiField("prefix", mf => mf.Text().Analyzer("i_prefix").SearchAnalyzer("q_prefix"))
        .MultiField("joined", mf => mf.Text().Analyzer("i_text_bigram").SearchAnalyzer("q_text_bigram"))
        .MultiField("regex", mf => mf.Wildcard());
}
