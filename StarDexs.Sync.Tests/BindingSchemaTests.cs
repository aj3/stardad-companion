using System.Text;
using StarDexs.Sync.Core;
using Xunit;
namespace StarDexs.Sync.Tests;
public sealed class BindingSchemaTests
{
    private static byte[] Export(string input)=>Encoding.UTF8.GetBytes($"<ActionMaps><actionmap name='synthetic'><action name='fixture'><rebind input='{input}'/></action></actionmap></ActionMaps>");
    private static byte[] Active(string body)=>Encoding.UTF8.GetBytes("<ActionMaps><ActionProfiles version='1' optionsVersion='2' rebindVersion='2' profileName='default'>"+body+"</ActionProfiles></ActionMaps>");
    [Theory][InlineData("js1_ ")][InlineData("kb1_")][InlineData("mo1_ ")]
    public void Cleared_device_slots_are_not_populated_exports(string input)=>Assert.Throws<InvalidDataException>(()=>KeybindFiles.Validate("layout_fixture_exported.xml",Export(input)));
    [Fact]public void Cleared_joystick_slots_do_not_require_a_joystick_for_keyboard_recovery(){var xml=Active("<actionmap name='synthetic'><action name='cleared'><rebind input='js1_ '/></action><action name='keyboard'><rebind input='kb1_fixture'/></action></actionmap>");Assert.Equal(1,ActiveControlRecovery.BindingCount(xml));Assert.False(ActiveControlRecovery.UsesJoystick(xml));}
    [Fact]public void Active_and_exported_profile_shapes_are_not_interchangeable(){var active=Active("<actionmap name='synthetic'><action name='keyboard'><rebind input='kb1_fixture'/></action></actionmap>");Assert.Throws<InvalidDataException>(()=>KeybindFiles.Validate("layout_fixture_exported.xml",active));Assert.Throws<InvalidDataException>(()=>ActiveControlRecovery.BindingCount(Export("kb1_fixture")));}
}
