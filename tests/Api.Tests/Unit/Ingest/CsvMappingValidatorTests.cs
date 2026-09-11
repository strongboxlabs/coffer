using Coffer.Api.Ingest.Csv;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// The gate for hand-editable mapping documents (mig 222).
/// </summary>
/// <remarks>
/// Migration 222 stores the definition as YAML rather than typed columns, so the
/// database asserts almost nothing about it. Every constraint the column design would
/// have enforced lives in the validator, which makes these tests the only thing standing
/// between a typo and an importer reading the wrong column as money.
/// </remarks>
public sealed class CsvMappingValidatorTests
{
    /// <summary>
    /// The shape of the first real target: a department-store card export. Tab-delimited
    /// despite a .csv name, no header row, a currency symbol in the amount, and amounts
    /// signed from the issuer's perspective so a purchase reads positive.
    /// Synthesised — no real statement data.
    /// </summary>
    private const string StoreCardYaml = """
        version: 1
        delimiter: tab
        header_rows: 0
        footer_rows: 0
        date:
          column: 1
          format: MM/dd/yyyy
        payee:
          column: 3
        amount:
          shape: signed
          column: 2
          invert: true
        """;

    [Fact]
    public void A_real_world_shape_validates_and_keeps_every_value()
    {
        var mapping = CsvMappingValidator.Validate(StoreCardYaml, out var errors);

        Assert.Empty(errors);
        Assert.NotNull(mapping);
        Assert.Equal(CsvDelimiter.Tab, mapping!.Delimiter);
        Assert.Equal('\t', mapping.Separator);
        Assert.Equal(0, mapping.HeaderRows);
        Assert.Equal(1, mapping.DateColumn);
        Assert.Equal("MM/dd/yyyy", mapping.DateFormat);
        Assert.Equal(3, mapping.PayeeColumn);
        Assert.Null(mapping.MemoColumn);
        Assert.Equal(CsvAmountShape.Signed, mapping.AmountShape);
        Assert.Equal(2, mapping.AmountColumn);
        // The one that decides whether money lands the right way round.
        Assert.True(mapping.AmountInvert);
    }

    [Fact]
    public void A_mistyped_key_is_an_error_and_not_a_shrug()
    {
        // THE reason this validator is strict. A document reader ignores what it does not
        // recognise, so `ammount` would leave the amount unmapped — and the importer
        // would read some other column as money without ever saying so.
        var yaml = StoreCardYaml.Replace("amount:", "ammount:", StringComparison.Ordinal);

        var mapping = CsvMappingValidator.Validate(yaml, out var errors);

        Assert.Null(mapping);
        Assert.Contains(errors, e => e.Path == "ammount" && e.Message.Contains("Unknown key"));
        // ...and it still reports the consequence — the real key is now missing.
        Assert.Contains(errors, e => e.Path == "amount" && e.Message.Contains("Required"));
    }

    [Fact]
    public void A_key_that_belongs_to_the_OTHER_amount_shape_is_rejected()
    {
        // `invert` means nothing under debit_credit: there is no sign to flip. Accepting
        // it silently would leave its author believing the importer does something it
        // never does — the same silent-misconfiguration failure as a typo.
        var yaml = """
            version: 1
            delimiter: comma
            date:
              column: 1
              format: yyyy-MM-dd
            payee:
              column: 2
            amount:
              shape: debit_credit
              debit_column: 3
              credit_column: 4
              invert: true
            """;

        var mapping = CsvMappingValidator.Validate(yaml, out var errors);

        Assert.Null(mapping);
        Assert.Contains(errors, e => e.Path == "amount.invert" && e.Message.Contains("Unknown key"));
    }

    [Fact]
    public void The_debit_credit_shape_validates_on_its_own_terms()
    {
        // Kept honest against the test above: the shape itself is fine, only `invert`
        // was wrong there.
        var yaml = """
            version: 1
            delimiter: semicolon
            header_rows: 1
            date:
              column: 1
              format: dd.MM.yyyy
            payee:
              column: 2
            memo:
              column: 5
            amount:
              shape: debit_credit
              debit_column: 3
              credit_column: 4
            """;

        var mapping = CsvMappingValidator.Validate(yaml, out var errors);

        Assert.Empty(errors);
        Assert.Equal(CsvAmountShape.DebitCredit, mapping!.AmountShape);
        Assert.Equal(3, mapping.DebitColumn);
        Assert.Equal(4, mapping.CreditColumn);
        Assert.Equal(5, mapping.MemoColumn);
        Assert.Null(mapping.AmountColumn);
        Assert.False(mapping.AmountInvert);
    }

    [Fact]
    public void Column_zero_is_refused_rather_than_treated_as_the_first_column()
    {
        // Columns are 1-based because the wizard says "column 1". A 0 accepted as an
        // index would read a different field than its author meant, and in an amount
        // position that means importing a date as money.
        var yaml = StoreCardYaml.Replace("column: 2", "column: 0", StringComparison.Ordinal);

        var mapping = CsvMappingValidator.Validate(yaml, out var errors);

        Assert.Null(mapping);
        Assert.Contains(errors, e => e.Path == "amount.column" && e.Message.Contains("numbered from 1"));
    }

    [Fact]
    public void One_field_cannot_be_both_the_date_and_the_amount()
    {
        var yaml = StoreCardYaml.Replace("column: 2", "column: 1", StringComparison.Ordinal);

        var mapping = CsvMappingValidator.Validate(yaml, out var errors);

        Assert.Null(mapping);
        Assert.Contains(errors, e => e.Path == "amount.column" && e.Message.Contains("already the date column"));
    }

    [Fact]
    public void A_date_format_nothing_can_read_back_fails_here_not_once_per_row()
    {
        // Otherwise the mapping looks fine and the IMPORT fails on every line, which
        // reads as "the file is broken" rather than "the mapping is".
        var yaml = StoreCardYaml.Replace("format: MM/dd/yyyy", "format: nonsense", StringComparison.Ordinal);

        var mapping = CsvMappingValidator.Validate(yaml, out var errors);

        Assert.Null(mapping);
        Assert.Contains(errors, e => e.Path == "date.format");
    }

    [Fact]
    public void Every_problem_is_reported_at_once_and_not_just_the_first()
    {
        // Someone hand-editing YAML one error per round trip is a bad afternoon, and an
        // MCP client iterating on a mapping wants the whole list in one response.
        var yaml = """
            version: 1
            delimiter: pipes
            date:
              column: 0
            payee:
              column: -1
            amount:
              shape: sideways
            """;

        var mapping = CsvMappingValidator.Validate(yaml, out var errors);

        Assert.Null(mapping);
        // delimiter, date.column, date.format missing, payee.column, amount.shape.
        Assert.True(errors.Count >= 5, "expected several problems, got: "
            + string.Join(" | ", errors.Select(e => e.Path + ": " + e.Message)));
        Assert.Contains(errors, e => e.Path == "delimiter");
        Assert.Contains(errors, e => e.Path == "date.format" && e.Message.Contains("Required"));
        Assert.Contains(errors, e => e.Path == "amount.shape");
    }

    [Fact]
    public void An_error_carries_the_line_so_it_can_be_found_in_an_editor()
    {
        // A document meant to be hand-edited has to say WHERE. The delimiter is on line 2.
        var mapping = CsvMappingValidator.Validate(
            StoreCardYaml.Replace("delimiter: tab", "delimiter: tabs", StringComparison.Ordinal),
            out var errors);

        Assert.Null(mapping);
        var problem = Assert.Single(errors, e => e.Path == "delimiter");
        Assert.Equal(2, problem.Line);
    }

    [Fact]
    public void Malformed_yaml_is_reported_as_malformed_yaml()
    {
        var mapping = CsvMappingValidator.Validate("version: 1\n  bad: [indent", out var errors);

        Assert.Null(mapping);
        Assert.Contains(errors, e => e.Message.Contains("Not valid YAML"));
    }

    [Fact]
    public void A_document_from_a_future_version_is_refused_by_name()
    {
        // schema_version exists so a stored document is read by the validator that
        // understood it. Refusing loudly beats reading version 2 keys as unknown ones.
        var yaml = StoreCardYaml.Replace("version: 1", "version: 2", StringComparison.Ordinal);

        var mapping = CsvMappingValidator.Validate(yaml, out var errors);

        Assert.Null(mapping);
        Assert.Contains(errors, e => e.Path == "version" && e.Message.Contains("Unsupported version 2"));
    }

    [Fact]
    public void An_empty_document_is_not_a_valid_mapping()
    {
        Assert.Null(CsvMappingValidator.Validate("", out var errors));
        Assert.NotEmpty(errors);

        Assert.Null(CsvMappingValidator.Validate("just a string", out var scalarErrors));
        Assert.NotEmpty(scalarErrors);
    }

    /// <summary>
    /// The document the web wizard writes, verbatim — guidance comments and all.
    /// </summary>
    /// <remarks>
    /// The YAML tab exists so someone can go past the form, and a bare document is a
    /// poor thing to hand them: an unused optional key is invisible, and the amount
    /// block has two shapes neither of which hints at the other. So the wizard writes
    /// the options out as comments. That only works if the gate ignores every one of
    /// them, which is what these two tests are for.
    /// </remarks>
    private const string WizardYaml = """
        # Coffer CSV mapping. Anything after a # is a comment.
        # Columns are numbered from 1, left to right, as in the grid above.
        version: 1

        # comma | tab | semicolon | pipe
        delimiter: tab

        header_rows: 0  # rows to skip before the data starts
        # footer_rows: 1  # skip totals or a disclaimer at the end of the file

        date:
          column: 1
          # Any .NET format that spells out a year, a month and a day.
          # M is month; lowercase m is MINUTES.
          # MM/dd/yyyy | yyyy-MM-dd | dd/MM/yyyy | dd.MM.yyyy | dd-MMM-yyyy
          format: MM/dd/yyyy

        payee:
          column: 3

        # memo:  # a second description column, if the file has one
        #   column: 4

        amount:
          # signed: one column carrying a +/- sign.
          # debit_credit: two columns, one of them empty on every row.
          shape: signed
          column: 2
          # true when a purchase is written POSITIVE, as card exports write it.
          # Coffer records a purchase as money out, so those get flipped.
          invert: true
          # Two columns instead? Swap shape/column/invert for:
          #   shape: debit_credit
          #   debit_column: 3
          #   credit_column: 4
        """;

    [Fact]
    public void Comments_are_guidance_and_never_reach_the_gate()
    {
        // Same file, same mapping, whether or not it explains itself. Note what the
        // comments contain: `footer_rows`, `memo` and `debit_credit` all appear as
        // commented-out suggestions, and every one of them would be REJECTED as a key
        // here — footer_rows and memo are absent from the bare document, and
        // debit_credit's keys are refused under `shape: signed`. If comments were read
        // as content, this document could not validate at all.
        var commented = CsvMappingValidator.Validate(WizardYaml, out var errors);
        var bare = CsvMappingValidator.Validate(StoreCardYaml, out _);

        Assert.Empty(errors);
        Assert.NotNull(commented);
        Assert.Equal(bare, commented);
    }

    [Fact]
    public void The_alternative_the_comments_offer_is_one_a_person_can_really_uncomment()
    {
        // A comment that suggests a document the gate refuses is worse than no comment
        // at all: it reads as instructions and ends in an error the reader did not
        // cause. So do exactly what it says — drop shape/column/invert, uncomment the
        // three lines offered in their place — and require the result to work.
        var swapped = WizardYaml
            .Replace("  shape: signed", "", StringComparison.Ordinal)
            .Replace("  column: 2", "", StringComparison.Ordinal)
            .Replace("  invert: true", "", StringComparison.Ordinal)
            .Replace("  #   shape: debit_credit", "  shape: debit_credit", StringComparison.Ordinal)
            .Replace("  #   debit_column: 3", "  debit_column: 3", StringComparison.Ordinal)
            .Replace("  #   credit_column: 4", "  credit_column: 4", StringComparison.Ordinal);

        var mapping = CsvMappingValidator.Validate(swapped, out var errors);

        Assert.Empty(errors);
        Assert.NotNull(mapping);
        Assert.Equal(CsvAmountShape.DebitCredit, mapping!.AmountShape);
        Assert.Equal(3, mapping.DebitColumn);
        Assert.Equal(4, mapping.CreditColumn);
    }
}
