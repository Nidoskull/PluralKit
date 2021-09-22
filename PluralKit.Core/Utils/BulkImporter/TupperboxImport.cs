using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;

using NodaTime;

namespace PluralKit.Core
{
    public partial class BulkImporter
    {
        private async Task<ImportResultNew> ImportTupperbox(JObject importFile)
        {
            var tuppers = importFile.Value<JArray>("tuppers");
            var newMembers = tuppers.Count(t => !_existingMemberNames.TryGetValue("name", out var memberId));
            await AssertMemberLimitNotReached(newMembers);

            string lastSetTag = null;
            bool multipleTags = false;
            bool hasGroup = false;

            foreach (JObject tupper in tuppers)
                (lastSetTag, multipleTags, hasGroup) = await ImportTupper(tupper, lastSetTag);

            if (multipleTags || hasGroup)
            {
                var issueStr =
                    $"{Emojis.Warn} The following potential issues were detected converting your Tupperbox input file:";
                if (hasGroup)
                    issueStr +=
                        "\n- PluralKit does not support member groups. Members will be imported without groups.";
                if (multipleTags)
                    issueStr +=
                        "\n- PluralKit does not support per-member system tags. Since you had multiple members with distinct tags, those tags will be applied to the members' *display names*/nicknames instead.";

                await _confirmFunc(issueStr);
                _result.Success = true;
            }

            return _result;
        }

        private async Task<(string lastSetTag, bool multipleTags, bool hasGroup)> ImportTupper(JObject tupper, string lastSetTag)
        {
            if (!tupper.ContainsKey("name") || tupper["name"].Type == JTokenType.Null)
                throw new ImportException("Field 'name' cannot be null.");

            var hasGroup = tupper.ContainsKey("group_id") && tupper["group_id"].Type != JTokenType.Null;
            var multipleTags = false;

            var name = tupper.Value<string>("name");
            var patch = new MemberPatch();

            patch.Name = name;
            if (tupper.ContainsKey("avatar_url") && tupper["avatar_url"].Type != JTokenType.Null) patch.AvatarUrl = tupper.Value<string>("avatar_url").NullIfEmpty();
            if (tupper.ContainsKey("brackets"))
            {
                var brackets = tupper.Value<JArray>("brackets");
                if (brackets.Count % 2 != 0)
                    throw new ImportException($"Field 'brackets' in tupper {name} is invalid.");
                var tags = new List<ProxyTag>();
                for (var i = 0; i < brackets.Count / 2; i++)
                    tags.Add(new ProxyTag((string)brackets[i * 2], (string)brackets[i * 2 + 1]));
                patch.ProxyTags = tags.ToArray();
            }
            // todo: && if is new member
            if (tupper.ContainsKey("posts")) patch.MessageCount = tupper.Value<int>("posts");
            if (tupper.ContainsKey("show_brackets")) patch.KeepProxy = tupper.Value<bool>("show_brackets");
            if (tupper.ContainsKey("birthday") && tupper["birthday"].Type != JTokenType.Null)
            {
                var parsed = DateTimeFormats.TimestampExportFormat.Parse(tupper.Value<string>("birthday"));
                if (!parsed.Success)
                    throw new ImportException($"Field 'birthday' in tupper {name} is invalid.");
                patch.Birthday = LocalDate.FromDateTime(parsed.Value.ToDateTimeUtc());
            }
            if (tupper.ContainsKey("description")) patch.Description = tupper.Value<string>("description");
            if (tupper.ContainsKey("tag") && tupper["tag"].Type != JTokenType.Null)
            {
                var tag = tupper.Value<string>("tag");
                if (tag != lastSetTag)
                {
                    lastSetTag = tag;
                    multipleTags = true;
                }
                patch.DisplayName = $"{name} {tag}";
            }

            var isNewMember = false;
            if (!_existingMemberNames.TryGetValue(name, out var memberId))
            {
                var newMember = await _repo.CreateMember(_conn, _system.Id, name, _tx);
                memberId = newMember.Id;
                isNewMember = true;
                _result.Added++;
            }
            else
                _result.Modified++;

            _logger.Debug("Importing member with identifier {FileId} to system {System} (is creating new member? {IsCreatingNewMember})",
                name, _system.Id, isNewMember);

            try
            {
                patch.AssertIsValid();
            }
            catch (FieldTooLongError e)
            {
                throw new ImportException($"Field {e.Name} in tupper {name} is too long ({e.ActualLength} > {e.MaxLength}).");
            }
            catch (ValidationError e)
            {
                throw new ImportException($"Field {e.Message} in tupper {name} is invalid.");
            }

            await _repo.UpdateMember(_conn, memberId, patch, _tx);

            return (lastSetTag, multipleTags, hasGroup);
        }
    }
}