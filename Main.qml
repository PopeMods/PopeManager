import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts

ApplicationWindow {
    id: window
    visible: true
    title: "PopeManager"
    width: 720
    height: 500
    minimumWidth: 560
    minimumHeight: 420
    font.family: "Segoe UI"
    font.pixelSize: 14
    Material.theme: Material.Dark

    ManagerBackend { id: backend }
    property var state: ({ busy:false, scanning:false, hostInstalled:false, devices:[], selected:"", status:"", error:"", canInstall:false, log:"" })
    property int revision: -1
    property int guideStep: 0
    readonly property var headset: state.devices.find(function(device) { return device.serial === state.selected }) || null
    readonly property bool connected: !!headset && headset.state === "device" && headset.quest
    readonly property bool needsPermission: state.devices.some(function(device) { return device.state === "unauthorized" })
    readonly property var quests: state.devices.filter(function(device) { return device.state === "device" && device.quest })

    function poll() {
        var next = JSON.parse(backend.snapshot())
        if (next.revision !== revision) { state = next; revision = next.revision }
    }
    Component.onCompleted: { poll(); backend.refresh() }
    Timer { interval: 250; repeat: true; running: true; onTriggered: window.poll() }
    Timer { interval: 5000; repeat: true; running: !window.state.busy; onTriggered: backend.refresh() }

    StackLayout {
        anchors.fill: parent
        currentIndex: window.connected ? 1 : 0

        Item {
            Label { anchors.left: parent.left; anchors.top: parent.top; anchors.margins: 28; text: "PopeManager"; font.pixelSize: 22 }
            ColumnLayout {
                anchors.horizontalCenter: parent.horizontalCenter
                anchors.top: parent.top
                anchors.topMargin: 108
                width: Math.min(parent.width - 80, 500)
                spacing: 18

                Label {
                    Layout.fillWidth: true
                    font.pixelSize: 28
                    wrapMode: Text.Wrap
                    text: window.needsPermission ? "Allow USB debugging" :
                        ["Plug in your headset", "Create an organization", "Enable developer mode", "Connect your headset"][window.guideStep]
                }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.Wrap
                    lineHeight: 1.35
                    color: "#d0d0d0"
                    text: window.needsPermission ? "Put on the headset and accept the USB debugging prompt." :
                        ["Connect your Quest with a USB cable to get started. If you need a hand, run through the connection guide.",
                         "Create an organization on the Meta Developer Hub. Sign in with the same account your headset uses.",
                         "In the Meta Horizon mobile app, go to Devices -> select your device -> Headset settings -> Developer mode, then turn it on.",
                         "Plug your headset into this PC with a USB cable. It should be detected automatically."][window.guideStep]
                }
                Button {
                    visible: window.guideStep === 1 && !window.needsPermission
                    text: "Open Meta Developer Hub"
                    flat: true
                    onClicked: Qt.openUrlExternally("https://developers.meta.com/vr/manage/organizations")
                }
                RowLayout {
                    spacing: 12
                    Button { visible: window.guideStep > 0 && !window.needsPermission; text: "Back"; flat: true; onClicked: window.guideStep-- }
                    Button { visible: window.guideStep < 3 && !window.needsPermission; text: window.guideStep === 0 ? "Connection guide" : "Next"; highlighted: true; onClicked: window.guideStep++ }
                    Button { text: "Check connection"; flat: true; enabled: !window.state.scanning; onClicked: backend.refresh() }
                    BusyIndicator { running: window.state.scanning; visible: running; implicitWidth: 28; implicitHeight: 28 }
                }
            }
        }

        Item {
            Label { anchors.left: parent.left; anchors.top: parent.top; anchors.margins: 28; text: "PopeManager"; font.pixelSize: 22 }
            ColumnLayout {
                anchors.horizontalCenter: parent.horizontalCenter
                anchors.top: parent.top
                anchors.topMargin: 108
                width: Math.min(parent.width - 80, 500)
                spacing: 18

                Label {
                    Layout.fillWidth: true
                    font.pixelSize: 28
                    wrapMode: Text.Wrap
                    text: "Quest connected"
                }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.Wrap
                    lineHeight: 1.35
                    color: "#d0d0d0"
                    text: window.headset.name + (window.headset.version ? " - Gorilla Tag " + window.headset.version : "") + (window.state.hostInstalled ? "\nAlready patched." : "")
                }
                ComboBox {
                    visible: window.quests.length > 1
                    Layout.fillWidth: true
                    model: window.quests
                    textRole: "name"
                    displayText: currentIndex < 0 ? "Choose headset" : currentText
                    currentIndex: -1
                    onActivated: function(index) { backend.selectDevice(model[index].serial) }
                }
                Button {
                    text: window.state.busy ? "Patching..." : window.state.hostInstalled ? "Patch again" : "Patch Gorilla Tag"
                    highlighted: true
                    enabled: window.state.canInstall
                    onClicked: backend.install()
                }
                BusyIndicator {
                    running: window.state.busy
                    visible: running
                    implicitWidth: 28
                    implicitHeight: 28
                }
                Label {
                    Layout.fillWidth: true
                    visible: !!window.state.error
                    wrapMode: Text.Wrap
                    color: Material.color(Material.Red, Material.Shade200)
                    text: window.state.error
                }
                Label { visible: !!window.state.log; text: "Patch log"; color: "#bdbdbd"; font.pixelSize: 12 }
                Rectangle {
                    visible: !!window.state.log
                    Layout.fillWidth: true
                    Layout.preferredHeight: 128
                    color: Material.color(Material.Grey, Material.Shade900)
                    radius: 2
                    ScrollView {
                        anchors.fill: parent
                        anchors.margins: 8
                        clip: true
                        TextArea {
                            text: window.state.log
                            readOnly: true
                            selectByMouse: true
                            wrapMode: TextEdit.WrapAnywhere
                            font.family: "Consolas"
                            font.pixelSize: 11
                            background: null
                            onTextChanged: cursorPosition = length
                        }
                    }
                }
                Button {
                    flat: true
                    text: "Refresh connection"
                    enabled: !window.state.busy && !window.state.scanning
                    onClicked: backend.refresh()
                }
            }
        }
    }
}
