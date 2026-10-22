using Eyu.Core.Proposals;
using Eyu.Core.Records;
using Saem.Ontology;

namespace Saem.Authoring;

/// <summary>
/// Turns what Eyu proposed about some records into declarations in saem's form. Eyu speaks of
/// entities — this record is a supplier, that work order names this item — and saem's ontology
/// speaks of types: which table a supplier stands on, which field of a work order leads to an item.
/// The drafter reads the second off the first, from the records the proposal cites:
/// <list type="bullet">
/// <item><description>
/// <b>An entity type</b> stands on the table of the subject whose records denote its entities. Its key
/// is that subject's key field, and its fields are the other fields those records carry. A type whose
/// entities are denoted by records of two subjects, or only mentioned, has no one table and is not
/// declared.
/// </description></item>
/// <item><description>
/// <b>A relation</b> leaves its source type through the field whose value, in every proposed instance,
/// is the key of the entity it leads to. A relation no field carries, or more than one field could
/// carry, is not declared. A name proposed between several pairs of types — a relation also stated two
/// hops away, through the entity in between — is read pair by pair, and only a pair a field carries
/// becomes the declaration.
/// </description></item>
/// </list>
/// What is already declared is the authority: an existing declaration is never changed, and where the
/// proposal places something differently, the draft keeps the declaration and says so in a note.
/// Nothing the proposal said is dropped silently — every element it could not declare is a
/// <see cref="DraftNote"/> naming the records it cited.
/// </summary>
public static class DeclarationDrafter
{
    /// <summary>Drafts declarations from a proposal.</summary>
    /// <param name="proposal">What Eyu proposed.</param>
    /// <param name="records">The records the proposal was made from — each carrying the subject it is of.</param>
    /// <param name="sources">For each subject, by name, the source table its records are rows of.</param>
    /// <param name="existing">The declarations already in force; omitted, none.</param>
    /// <exception cref="OntologyDeclarationException">The existing declarations are not consistent.</exception>
    public static DeclarationDraft Draft(
        OntologyProposal proposal,
        IReadOnlyList<RawRecord> records,
        IReadOnlyDictionary<string, SubjectSource> sources,
        IReadOnlyList<Declaration>? existing = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sources);
        existing ??= [];

        var declared = Ontology.Ontology.Create(existing);
        var drafting = new Drafting(proposal, records, sources, declared);
        var types = drafting.DraftTypes();
        var relations = drafting.DraftRelations(types);

        var added = types.Cast<Declaration>().Concat(relations).ToList();
        var all = existing.Concat(added).ToList();

        // A draft the catalog would refuse is a fault here, not something for the reviewer to find.
        Ontology.Ontology.Create(all);

        return new DeclarationDraft(all, added, drafting.Evidence, drafting.Notes);
    }

    private sealed class Drafting(
        OntologyProposal proposal,
        IReadOnlyList<RawRecord> records,
        IReadOnlyDictionary<string, SubjectSource> sources,
        Ontology.Ontology declared)
    {
        private readonly Dictionary<string, RawRecord> _records = records
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        private readonly Dictionary<string, EntityProposal> _entities = proposal.Entities
            .GroupBy(e => e.EntityId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        public Dictionary<string, IReadOnlyList<string>> Evidence { get; } = new(StringComparer.Ordinal);

        public List<DraftNote> Notes { get; } = [];

        public List<EntityTypeDeclaration> DraftTypes()
        {
            var drafted = new List<EntityTypeDeclaration>();
            foreach (var group in proposal.Entities.GroupBy(e => e.EntityType, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var type = group.Key;
                var cited = Distinct(group.SelectMany(e => e.Claim.Sources.Select(s => s.RecordId)));
                var denoting = Distinct(group.SelectMany(e => e.DenotedBy));
                if (denoting.Count == 0)
                {
                    Note(type, "No record denotes an entity of this type — the records only mention one — so there is no table it stands on.", cited);
                    continue;
                }

                if (!TryPlace(type, denoting, out var source, out var rows))
                {
                    continue;
                }

                var fields = rows.SelectMany(r => r.Fields.Keys)
                    .Where(f => !string.Equals(f, source.KeyField, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToDictionary(f => f, source.ColumnOf, StringComparer.Ordinal);
                var declaration = new EntityTypeDeclaration(type, source.System, source.Table, source.ColumnOf(source.KeyField), fields);

                if (declared.EntityTypes.TryGetValue(type, out var existing))
                {
                    NoteDifference(existing, declaration, denoting);
                    continue;
                }

                drafted.Add(declaration);
                Evidence[type] = denoting;
            }

            return drafted;
        }

        public List<RelationDeclaration> DraftRelations(IReadOnlyList<EntityTypeDeclaration> draftedTypes)
        {
            var types = declared.EntityTypes.Values.Concat(draftedTypes).ToDictionary(t => t.Name, StringComparer.Ordinal);
            var drafted = new List<RelationDeclaration>();

            foreach (var group in proposal.Relations.GroupBy(r => r.RelationName, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var name = group.Key;
                var instances = group
                    .Select(r => (Relation: r, From: _entities.GetValueOrDefault(r.FromEntityId), To: _entities.GetValueOrDefault(r.ToEntityId)))
                    .Where(i => i.From is not null && i.To is not null)
                    .Select(i => (i.Relation, From: i.From!, To: i.To!))
                    .ToList();
                if (instances.Count == 0)
                {
                    Note(name, "The relation names no entity the proposal holds.", Cited(group));
                    continue;
                }

                // A name may be proposed between several pairs of types — a model that also states the
                // relation two hops away, through the entity in between, under the same name. Each pair
                // is read on its own: a pair no field carries is noted, and only a pair a field carries
                // can become the declaration.
                var pairs = instances.GroupBy(i => (From: i.From.EntityType, To: i.To.EntityType)).ToList();
                var readable = new List<(RelationDeclaration Declaration, List<string> Cited)>();
                foreach (var pair in pairs)
                {
                    var label = pairs.Count > 1 ? $"{pair.Key.From} → {pair.Key.To}: " : "";
                    var pairInstances = pair.ToList();
                    var cited = Cited(pairInstances.Select(i => i.Relation));
                    if (TryRead(name, label, pair.Key.From, pair.Key.To, pairInstances, cited, types) is { } declaration)
                    {
                        readable.Add((declaration, cited));
                    }
                }

                if (readable.Count > 1)
                {
                    Note(name, $"Fields carry it between more than one pair of types ({string.Join(", ", readable.Select(r => Describe(r.Declaration)))}); a declared name is one relation — declare each under its own name.", Cited(group));
                    continue;
                }

                if (readable.Count == 0)
                {
                    continue;
                }

                var (relation, evidence) = readable[0];
                if (declared.Relations.TryGetValue(name, out var existing))
                {
                    if (existing != relation)
                    {
                        Note(name, $"Already declared as {Describe(existing)}; the proposal reads it as {Describe(relation)}. The declaration is kept.", evidence);
                    }

                    continue;
                }

                var same = declared.Relations.Values.FirstOrDefault(r => r.From == relation.From && r.Via == relation.Via && r.To == relation.To);
                if (same is not null)
                {
                    Note(name, $"Already declared under the name '{same.Name}'. The declaration is kept.", evidence);
                    continue;
                }

                drafted.Add(relation);
                Evidence[name] = evidence;
            }

            return drafted;
        }

        /// <summary>
        /// The declaration one pair of types reads as — or null, with a note, when a type is undeclared,
        /// no single field carries the relation, or the field is not one the source type declares.
        /// </summary>
        private RelationDeclaration? TryRead(
            string name,
            string label,
            string fromType,
            string toType,
            IReadOnlyList<(RelationProposal Relation, EntityProposal From, EntityProposal To)> instances,
            IReadOnlyList<string> cited,
            Dictionary<string, EntityTypeDeclaration> types)
        {
            if (!types.TryGetValue(fromType, out var from) || !types.ContainsKey(toType))
            {
                var missing = types.ContainsKey(fromType) ? toType : fromType;
                Note(name, $"{label}It joins '{fromType}' and '{toType}', but '{missing}' is not declared — declare the type first.", cited);
                return null;
            }

            if (!TryFindVia(name, label, instances, cited, out var viaField, out var fromSource))
            {
                return null;
            }

            // The field the relation leaves through, by its declared name: the field of the source
            // type held by the column the instances showed.
            var column = fromSource.ColumnOf(viaField);
            var via = from.Fields.FirstOrDefault(f => string.Equals(f.Value, column, StringComparison.Ordinal)).Key;
            if (via is null)
            {
                Note(name, $"{label}Its value is held by column '{column}', which no field of '{fromType}' declares — declare the field first.", cited);
                return null;
            }

            return new RelationDeclaration(name, fromType, via, toType);
        }

        /// <summary>
        /// The one source table the denoting records stand on — or false, with a note, when they are
        /// missing, of no subject, of more than one subject, or of a subject no table was given for.
        /// </summary>
        private bool TryPlace(string type, IReadOnlyList<string> denoting, out SubjectSource source, out List<RawRecord> rows)
        {
            source = null!;
            rows = [];

            var unknown = denoting.Where(id => !_records.ContainsKey(id)).ToList();
            if (unknown.Count > 0)
            {
                Note(type, $"The proposal cites records it was not given: {string.Join(", ", unknown)}.", denoting);
                return false;
            }

            rows = denoting.Select(id => _records[id]).ToList();
            var subjects = rows.Select(r => r.Subject?.Value).Distinct(StringComparer.Ordinal).ToList();
            if (subjects.Contains(null))
            {
                Note(type, "A record that denotes it does not say which subject it is of, so the table it stands on is unknown.", denoting);
                return false;
            }

            if (subjects.Count > 1)
            {
                Note(type, $"Records of more than one subject denote it ({string.Join(", ", subjects.Order(StringComparer.Ordinal))}); a declared type stands on one table.", denoting);
                return false;
            }

            if (!sources.TryGetValue(subjects[0]!, out source!))
            {
                Note(type, $"Its records are of '{subjects[0]}', and no source table was given for that subject.", denoting);
                return false;
            }

            var keyField = source.KeyField;
            var keyless = rows.Where(r => KeyOf(r, keyField) is null).Select(r => r.Id).ToList();
            if (keyless.Count > 0)
            {
                Note(type, $"Records that denote it carry no value in the key field '{keyField}': {string.Join(", ", keyless)}.", denoting);
                return false;
            }

            return true;
        }

        /// <summary>
        /// The field of the source records that holds the target's key in every instance that shows
        /// one — or false, with a note, when no such field exists or more than one does.
        /// </summary>
        private bool TryFindVia(
            string name,
            string label,
            IReadOnlyList<(RelationProposal Relation, EntityProposal From, EntityProposal To)> instances,
            IReadOnlyList<string> cited,
            out string via,
            out SubjectSource fromSource)
        {
            via = null!;
            fromSource = null!;
            HashSet<string>? candidates = null;

            foreach (var (relation, from, to) in instances)
            {
                var keys = Rows(to.DenotedBy)
                    .Select(r => sources.TryGetValue(r.Subject!.Value.Value, out var s) ? KeyOf(r, s.KeyField) : null)
                    .OfType<string>()
                    .ToHashSet(StringComparer.Ordinal);
                var fromRows = Rows(from.DenotedBy)
                    .Where(r => sources.ContainsKey(r.Subject!.Value.Value))
                    .ToList();
                if (keys.Count == 0 || fromRows.Count == 0)
                {
                    // An end only mentioned shows no field and no key: this instance cannot say which
                    // field carries the relation, so it neither supports nor contradicts the others.
                    continue;
                }

                // Where the relation's claim cites the fields it read, only those fields are candidates.
                var citedFields = relation.Claim.Sources
                    .Where(s => s.FieldName is not null && fromRows.Any(r => r.Id == s.RecordId))
                    .Select(s => s.FieldName!)
                    .ToHashSet(StringComparer.Ordinal);

                fromSource = sources[fromRows[0].Subject!.Value.Value];
                var keyField = fromSource.KeyField;
                var holding = fromRows
                    .SelectMany(r => r.Fields.Where(f => f.Value is not null && keys.Contains(f.Value.Trim())).Select(f => f.Key))
                    .Where(f => !string.Equals(f, keyField, StringComparison.Ordinal))
                    .Where(f => citedFields.Count == 0 || citedFields.Contains(f))
                    .ToHashSet(StringComparer.Ordinal);

                if (candidates is null)
                {
                    candidates = holding;
                }
                else
                {
                    candidates.IntersectWith(holding);
                }
            }

            if (candidates is null)
            {
                Note(name, label + "No instance has both ends denoted by records, so no record shows which field carries the relation.", cited);
                return false;
            }

            if (candidates.Count != 1)
            {
                Note(name, label + (candidates.Count == 0
                    ? "No field of the source records holds the target's key in every instance."
                    : $"More than one field holds the target's key in every instance ({string.Join(", ", candidates.Order(StringComparer.Ordinal))})."), cited);
                return false;
            }

            via = candidates.Single();
            return true;
        }

        private void NoteDifference(EntityTypeDeclaration existing, EntityTypeDeclaration proposed, IReadOnlyList<string> denoting)
        {
            if (existing.System != proposed.System || existing.Table != proposed.Table || existing.Key != proposed.Key)
            {
                Note(existing.Name, $"Already declared on {existing.System}.{existing.Table} keyed by '{existing.Key}'; the proposal places it on {proposed.System}.{proposed.Table} keyed by '{proposed.Key}'. The declaration is kept.", denoting);
                return;
            }

            var columns = existing.Fields.Values.ToHashSet(StringComparer.Ordinal);
            var undeclared = proposed.Fields.Values.Where(c => !columns.Contains(c)).ToList();
            if (undeclared.Count > 0)
            {
                Note(existing.Name, $"Its records also carry columns no field declares: {string.Join(", ", undeclared)}.", denoting);
            }
        }

        private IEnumerable<RawRecord> Rows(IEnumerable<string> ids) =>
            ids.Select(id => _records.GetValueOrDefault(id)).OfType<RawRecord>().Where(r => r.Subject is not null);

        private void Note(string about, string reason, IReadOnlyList<string> records) => Notes.Add(new DraftNote(about, reason, records));

        private static string? KeyOf(RawRecord record, string keyField) =>
            record.Fields.TryGetValue(keyField, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        private static List<string> Cited(IEnumerable<RelationProposal> relations) =>
            Distinct(relations.SelectMany(r => r.Claim.Sources.Select(s => s.RecordId)));

        private static List<string> Distinct(IEnumerable<string> ids) => ids.Distinct(StringComparer.Ordinal).ToList();

        private static string Describe(RelationDeclaration r) => $"{r.From}.{r.Via} → {r.To}";
    }
}
