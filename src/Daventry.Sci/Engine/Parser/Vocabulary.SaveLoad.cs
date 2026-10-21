namespace Daventry.Sci.Engine.Parser;

public sealed partial class Vocabulary
{
    /// <summary>Port of Vocabulary::saveLoadWithSerializer: the synonyms set by kSetSynonyms.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        int count = s.SyncCount(_synonyms.Count);
        if (s.IsLoading)
        {
            _synonyms.Clear();
            for (int i = 0; i < count; i++)
            {
                ushort replaceant = s.SyncUInt16(0);
                ushort replacement = s.SyncUInt16(0);
                _synonyms.Add(new Synonym(replaceant, replacement));
            }
        }
        else
        {
            foreach (var synonym in _synonyms)
            {
                s.SyncUInt16((ushort)synonym.Replaceant);
                s.SyncUInt16((ushort)synonym.Replacement);
            }
        }
    }
}
