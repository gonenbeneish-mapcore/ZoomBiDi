using System.Runtime.InteropServices;

namespace ZoomBiDi;

// Minimal direct bindings to the Windows UI Automation COM API (UIAutomationClient.h). Using it directly instead of
// the managed System.Windows.Automation wrapper avoids loading WPF, which roughly halves the app's memory use.
//
// COM calls go by vtable slot, so every interface declares its methods in the exact order of the header, with
// placeholder methods ("_SlotN") for the ones we never call. Slots 0-2 are IUnknown.
// IIDs and slot order verified against UIAutomationClient.h (Windows SDK 10.0.26100).

internal static class Uia
{
    public const int UIA_ValuePatternId = 10002;
    public const int UIA_TextPatternId = 10014;
    public const int UIA_IsPasswordPropertyId = 30019;
    public const int UIA_EditControlTypeId = 50004;
    public const int UIA_MenuControlTypeId = 50009;
    public const int UIA_MenuBarControlTypeId = 50010;
    public const int UIA_MenuItemControlTypeId = 50011;
    public const int UIA_DocumentControlTypeId = 50030;
    public const int UIA_WindowControlTypeId = 50032;
    public const int UIA_PaneControlTypeId = 50033;

    public const int TextPatternRangeEndpoint_Start = 0;
    public const int TextPatternRangeEndpoint_End = 1;
    public const int TextUnit_Character = 0;
    public const int TextUnit_Line = 3;
}

[ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
internal class CUIAutomation { }

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    void _Slot3();  // CompareElements
    void _Slot4();  // CompareRuntimeIds
    void _Slot5();  // GetRootElement
    void _Slot6();  // ElementFromHandle
    void _Slot7();  // ElementFromPoint
    IUIAutomationElement GetFocusedElement(); // 8
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void _Slot3();  // SetFocus
    void _Slot4();  // GetRuntimeId
    void _Slot5();  // FindFirst
    void _Slot6();  // FindAll
    void _Slot7();  // FindFirstBuildCache
    void _Slot8();  // FindAllBuildCache
    void _Slot9();  // BuildUpdatedCache
    [return: MarshalAs(UnmanagedType.Struct)]
    object GetCurrentPropertyValue(int propertyId); // 10
    void _Slot11(); // GetCurrentPropertyValueEx
    void _Slot12(); // GetCachedPropertyValue
    void _Slot13(); // GetCachedPropertyValueEx
    void _Slot14(); // GetCurrentPatternAs
    void _Slot15(); // GetCachedPatternAs
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object? GetCurrentPattern(int patternId); // 16
    void _Slot17(); // GetCachedPattern
    void _Slot18(); // GetCachedParent
    void _Slot19(); // GetCachedChildren
    int get_CurrentProcessId(); // 20
    int get_CurrentControlType(); // 21
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_CurrentLocalizedControlType(); // 22
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_CurrentName(); // 23
}

[ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextPattern
{
    void _Slot3();  // RangeFromPoint
    void _Slot4();  // RangeFromChild
    IUIAutomationTextRangeArray GetSelection(); // 5
    void _Slot6();  // GetVisibleRanges
    IUIAutomationTextRange get_DocumentRange(); // 7
}

[ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextRangeArray
{
    int get_Length(); // 3
    IUIAutomationTextRange GetElement(int index); // 4
}

[ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextRange
{
    IUIAutomationTextRange Clone(); // 3
    void _Slot4();  // Compare
    void _Slot5();  // CompareEndpoints
    void ExpandToEnclosingUnit(int textUnit); // 6
    void _Slot7();  // FindAttribute
    void _Slot8();  // FindText
    void _Slot9();  // GetAttributeValue
    void _Slot10(); // GetBoundingRectangles
    void _Slot11(); // GetEnclosingElement
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetText(int maxLength); // 12
    int Move(int unit, int count); // 13
    int MoveEndpointByUnit(int endpoint, int unit, int count); // 14
    void MoveEndpointByRange(int srcEndPoint, IUIAutomationTextRange range, int targetEndPoint); // 15
    void Select(); // 16
}

[ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationValuePattern
{
    void _Slot3();  // SetValue
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_CurrentValue(); // 4
}
