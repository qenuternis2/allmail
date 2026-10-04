using System.Text;

namespace ProtonProfiles.Core.Model;

public sealed record ProfileGroup(Guid Id, string Name)
{
    public static string NormalizeName(string name)
    {
        var value = name.Trim().Normalize(NormalizationForm.FormC);
        if (value.Length is < 1 or > 80 || value.Any(char.IsControl))
            throw new ArgumentException("Название группы должно содержать от 1 до 80 символов без управляющих знаков.");
        return value;
    }
}
