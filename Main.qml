import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import QtQuick.Dialogs
import QtMultimedia

ApplicationWindow {
    id: window
    visible: true
    title: "PopeManager"
    width: patched ? 1040 : 720
    height: state.busy ? 620 : patched ? 740 : 500
    minimumWidth: patched ? 840 : 560
    minimumHeight: patched ? 600 : 420
    font.family: "Segoe UI"
    font.pixelSize: 14
    Material.theme: Material.Dark

    ManagerBackend { id: backend }
    property var state: ({ busy:false, scanning:false, hostInstalled:false, devices:[], selected:"", status:"", error:"", canInstall:false, log:"" })
    property string offeredUpdate: ""
    property var promptedHeadsets: []
    function autoPrepare() {
        // Do not restart cancelled or failed attempts until the headset reconnects.
        var connectedSerials = state.devices.filter(function(device) {
            return device.state === "device" && device.quest
        }).map(function(device) { return device.serial })
        promptedHeadsets = promptedHeadsets.filter(function(serial) {
            return connectedSerials.indexOf(serial) >= 0
        })
        if (!connected || patched || state.busy || state.scanning || !state.canInstall ||
                promptedHeadsets.indexOf(state.selected) >= 0) return false
        promptedHeadsets = promptedHeadsets.concat([state.selected])
        updateDialog.close()
        backend.install()
        return true
    }
    property int revision: -1
    property int guideStep: 0
    property int page: 0
    property bool displayLogs: false
    readonly property bool patched: connected && (state.installed || state.hostInstalled)
    readonly property bool canManage: patched && !state.busy && !state.scanning
    onConnectedChanged: { if (!connected) page = 0 }
    function canAcceptModDrop(urls) {
        if (!canManage || !urls || urls.length !== 1) return false
        var url = urls[0].toString()
        return /^file:\/\//i.test(url) && /\.dll$/i.test(url)
    }
    function installDroppedMod(urls) {
        if (!canAcceptModDrop(urls)) return false
        backend.importMod(urls[0].toString())
        return true
    }
    function loadSettings() {
        loaderPath.text = state.settings.loader || ""
        generatorPath.text = state.settings.generators || ""
        unityPath.text = state.settings.unity || ""
    }
    function showUpdatePreview() {
        aboutWindow.hide()
        devWindow.hide()
        window.show()
        window.raise()
        window.requestActivate()
        updateDialog.close()
        updateDialog.parent = window.contentItem
        updateDialog.releaseVersion = state.updateVersion || state.appVersion || "1.0.0"
        updateDialog.releaseUrl = state.updateUrl || "https://github.com/PopeMods/PopeManager/releases/latest"
        updateDialog.open()
    }
    MediaPlayer {
        id: puzzleSolved
        source: window.state.puzzleSoundUrl || ""
        audioOutput: AudioOutput {}
    }
    component Caption: Label { opacity: 0.7; font.pixelSize: 12 }
    component Surface: Pane { Material.elevation: 0; Material.background: Material.backgroundColor }
    readonly property var headset: state.devices.find(function(device) { return device.serial === state.selected }) || null
    readonly property bool connected: !!headset && headset.state === "device" && headset.quest
    readonly property bool needsPermission: state.devices.some(function(device) { return device.state === "unauthorized" })
    readonly property bool needsUsbAccess: state.devices.some(function(device) { return device.state === "no permissions" })
    readonly property var quests: state.devices.filter(function(device) { return device.state === "device" && device.quest })

    function poll() {
        var next = JSON.parse(backend.snapshot())
        if (next.revision !== revision) { state = next; revision = next.revision }
        if (autoPrepare()) return
        if (state.updateAvailable && !state.checkingUpdates && !state.busy && offeredUpdate !== state.updateVersion) {
            offeredUpdate = state.updateVersion
            updateDialog.releaseVersion = state.updateVersion
            updateDialog.releaseUrl = state.updateUrl
            updateDialog.parent = aboutWindow.visible ? aboutWindow.contentItem : window.contentItem
            updateDialog.open()
        }
    }
    Component.onCompleted: { poll(); backend.refresh(); backend.checkForUpdates() }
    Timer { interval: 250; repeat: true; running: true; onTriggered: window.poll() }
    Timer { interval: 5000; repeat: true; running: !window.state.busy; onTriggered: { if (window.patched) backend.refreshSilently(); else backend.refresh() } }

    StackLayout {
        id: mainPages
        anchors.fill: parent
        currentIndex: window.state.steamLogin ? 3 : window.state.busy && window.state.step >= 0 ? 4 : window.patched ? 2 : window.connected ? 1 : 0

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
                    text: window.needsUsbAccess ? "Grant USB access to your Quest" :
                        window.needsPermission ? "Allow USB debugging" :
                        ["Plug in your headset", "Create an organization", "Enable developer mode", "Connect your headset"][window.guideStep]
                }
                Label {
                    Layout.fillWidth: true
                    wrapMode: Text.Wrap
                    lineHeight: 1.35
                    color: "#d0d0d0"
                    text: window.needsUsbAccess ? "Linux is blocking access to the headset. Install the Android udev rules (or add your user to the plugdev group), then unplug and reconnect the headset." :
                        window.needsPermission ? "Put on the headset and accept the USB debugging prompt." :
                        ["Connect your Quest with a USB cable to get started. If you need a hand, run through the connection guide.",
                         "Create an organization on the Meta Developer Hub. Sign in with the same account your headset uses.",
                         "In the Meta Horizon mobile app, go to Devices -> select your device -> Headset settings -> Developer mode, then turn it on.",
                         "Plug your headset into this PC with a USB cable. It should be detected automatically."][window.guideStep]
                }
                Button {
                    visible: window.guideStep === 1 && !window.needsPermission && !window.needsUsbAccess
                    text: "Open Meta Developer Hub"
                    flat: true
                    onClicked: Qt.openUrlExternally("https://developers.meta.com/vr/manage/organizations")
                }
                RowLayout {
                    spacing: 12
                    Button { visible: window.guideStep > 0 && !window.needsPermission && !window.needsUsbAccess; text: "Back"; flat: true; onClicked: window.guideStep-- }
                    Button { visible: window.guideStep < 3 && !window.needsPermission && !window.needsUsbAccess; text: window.guideStep === 0 ? "Connection guide" : "Next"; highlighted: true; onClicked: window.guideStep++ }
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
                    text: (window.headset ? window.headset.name : "Quest") + (window.headset && window.headset.version ? " - Gorilla Tag " + window.headset.version : "") + (window.state.hostInstalled ? "\nAlready patched." : "")
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
                    text: window.state.busy ? "Preparing..." : window.state.hostInstalled ? "Patch again" : "Sign in with Steam"
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

        RowLayout {
            spacing: 0
            Pane {
                Layout.preferredWidth: 224
                Layout.fillHeight: true
                padding: 16
                Material.elevation: 0
                ColumnLayout {
                    anchors.fill: parent
                    spacing: 8
                    Label { Layout.topMargin: 12; Layout.leftMargin: 16; text: "PopeManager"; font.pixelSize: 22 }
                    Item { Layout.preferredHeight: 16 }
                    Repeater {
                        model: ["Mod management", "Tools and options"]
                        delegate: Button {
                            id: navButton
                            required property int index
                            required property string modelData
                            Layout.fillWidth: true
                            implicitHeight: 48
                            leftPadding: 16
                            rightPadding: 16
                            topInset: 0
                            bottomInset: 0
                            flat: true
                            checked: window.page === index
                            Material.roundedScale: Material.FullScale
                            Material.elevation: 0
                            text: modelData
                            Binding {
                                target: navButton.background
                                property: "color"
                                value: navButton.checked ? navButton.Material.listHighlightColor : "transparent"
                            }
                            contentItem: Label {
                                text: navButton.text
                                verticalAlignment: Text.AlignVCenter
                                elide: Text.ElideRight
                            }
                            onClicked: {
                                if (index === 1) window.loadSettings()
                                window.page = index
                            }
                        }
                    }
                    Item { Layout.fillHeight: true }
                    ComboBox {
                        Layout.fillWidth: true
                        visible: window.quests.length > 1
                        enabled: !window.state.busy && !window.state.scanning
                        model: window.quests
                        textRole: "name"
                        currentIndex: window.quests.findIndex(function(d) { return d.serial === window.state.selected })
                        onActivated: function(index) { backend.selectDevice(model[index].serial) }
                    }
                    Switch { text: "Display logs"; checked: window.displayLogs; onToggled: window.displayLogs = checked }
                    Caption { Layout.fillWidth: true; text: window.headset ? window.headset.name : ""; elide: Text.ElideRight }
                    Caption { text: "Gorilla Tag " + (window.headset ? window.headset.version || "" : "") }
                }
            }
            ColumnLayout {
                Layout.fillWidth: true
                Layout.fillHeight: true
                Layout.margins: 24
                spacing: 16
                RowLayout {
                    Layout.fillWidth: true
                    Label { text: window.page === 0 ? "Mods" : "Tools & options"; font.pixelSize: 24 }
                    Item { Layout.fillWidth: true }
                    BusyIndicator { running: window.state.scanning; visible: running; implicitWidth: 24; implicitHeight: 24 }
                    Button { text: "Refresh"; flat: true; enabled: window.canManage; onClicked: backend.refresh() }
                    Button { visible: window.page === 0; text: "Browse"; highlighted: true; enabled: window.canManage; onClicked: modBrowser.open() }
                }
                StackLayout {
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    currentIndex: window.page
                    Item {
                        ListView {
                            id: modList
                            anchors.fill: parent
                            model: window.state.mods || []
                            spacing: 8
                            clip: true
                            ScrollBar.vertical: ScrollBar {}
                            delegate: Surface {
                                required property var modelData
                                width: modList.width
                                height: 128
                                padding: 16
                                RowLayout {
                                    anchors.fill: parent
                                    spacing: 16
                                    Image {
                                        id: modIcon
                                        Layout.preferredWidth: 88
                                        Layout.preferredHeight: 88
                                        fillMode: Image.PreserveAspectFit
                                        source: modelData.icon || window.state.logoUrl || ""
                                        onStatusChanged: {
                                            if (status === Image.Error && source.toString() !== window.state.logoUrl)
                                                source = window.state.logoUrl
                                        }
                                    }
                                    ColumnLayout {
                                        Layout.fillWidth: true
                                        spacing: 2
                                        Label { Layout.fillWidth: true; text: modelData.name; font.pixelSize: 18; elide: Text.ElideRight }
                                        Caption { Layout.fillWidth: true; text: "BepInEx plugin"; elide: Text.ElideRight }
                                        Button {
                                            text: "Delete"; flat: true; enabled: window.canManage
                                            onClicked: { deleteMod.fileName = modelData.fileName; deleteMod.modName = modelData.name; deleteMod.open() }
                                        }
                                    }
                                    Switch {
                                        text: modelData.enabled ? "Enabled" : "Disabled"
                                        checked: modelData.enabled
                                        enabled: window.canManage
                                        onClicked: backend.setModEnabled(modelData.fileName, checked)
                                    }
                                }
                            }
                        }
                        ColumnLayout {
                            anchors.centerIn: parent
                            width: Math.max(0, parent.width - 48)
                            visible: modList.count === 0
                            spacing: 12
                            Label { Layout.alignment: Qt.AlignHCenter; text: "No mods installed"; font.pixelSize: 22 }
                            Label { Layout.fillWidth: true; horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap; opacity: 0.7; text: "Browse for a plugin DLL to add your first mod." }
                            Button { Layout.alignment: Qt.AlignHCenter; text: "Browse mods"; highlighted: true; enabled: window.canManage; onClicked: modBrowser.open() }
                        }
                    }
                    ScrollView {
                        id: toolsScroll
                        clip: true
                        contentWidth: availableWidth
                        ColumnLayout {
                            width: toolsScroll.availableWidth
                            spacing: 12
                            Label { text: "Game"; font.pixelSize: 18 }
                            Label { Layout.fillWidth: true; text: "Patch again after updating Gorilla Tag."; wrapMode: Text.Wrap; opacity: 0.7 }
                            Button { text: "Patch again"; enabled: window.state.canInstall; onClicked: backend.install() }
                            Label { Layout.topMargin: 16; text: "Patching settings"; font.pixelSize: 18 }
                            Label { text: "FusionCore APK" }
                            TextField { id: loaderPath; Layout.fillWidth: true; enabled: !window.state.busy; selectByMouse: true }
                            Label { text: "PC generator folder" }
                            TextField { id: generatorPath; Layout.fillWidth: true; enabled: !window.state.busy; selectByMouse: true }
                            Label { text: "Unity reference folder (optional)" }
                            TextField { id: unityPath; Layout.fillWidth: true; enabled: !window.state.busy; placeholderText: "Download automatically"; selectByMouse: true }
                            Button { text: "Save settings"; highlighted: true; enabled: !window.state.busy; onClicked: backend.saveSettings(loaderPath.text, generatorPath.text, unityPath.text) }
                            RowLayout {
                                Button { text: "Open log folder"; flat: true; onClicked: backend.openLogs() }
                                Button { text: "Open output folder"; flat: true; enabled: !!window.state.lastOutput; onClicked: backend.openOutput() }
                            }
                            Button {
                                Layout.topMargin: 16
                                text: "About"
                                flat: true
                                onClicked: { aboutWindow.show(); aboutWindow.raise(); aboutWindow.requestActivate() }
                            }
                        }
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    visible: !!window.state.error || window.state.busy || !!window.state.status
                    Label {
                        Layout.fillWidth: true
                        wrapMode: Text.Wrap
                        text: window.state.error || window.state.status
                        color: window.state.error ? Material.color(Material.Red, Material.Shade200) : Material.foreground
                        font.pixelSize: 12
                    }
                    BusyIndicator { visible: window.state.busy; running: visible; implicitWidth: 24; implicitHeight: 24 }
                    Button { visible: window.state.busy; text: "Cancel"; flat: true; onClicked: backend.cancel() }
                }
                Surface {
                    visible: window.displayLogs
                    Layout.fillWidth: true
                    Layout.preferredHeight: 180
                    padding: 12
                    ColumnLayout {
                        anchors.fill: parent
                        spacing: 4
                        RowLayout {
                            Label { text: "Modding log"; font.pixelSize: 16 }
                            Item { Layout.fillWidth: true }
                            Button { text: "Open folder"; flat: true; onClicked: backend.openLogs() }
                        }
                        ScrollView {
                            Layout.fillWidth: true
                            Layout.fillHeight: true
                            clip: true
                            TextArea {
                                text: window.state.log || ""
                                readOnly: true
                                selectByMouse: true
                                wrapMode: TextEdit.WrapAnywhere
                                font.family: "Consolas"
                                font.pixelSize: 12
                                background: null
                                onTextChanged: cursorPosition = length
                            }
                        }
                    }
                }
            }
        }
        Item {
            id: steamPage
            onVisibleChanged: { if (!visible) { steamPassword.clear(); steamCode.clear() } }
            ColumnLayout {
                anchors.fill: parent
                anchors.margins: 28
                spacing: 16
                Label { text: "PopeManager"; font.pixelSize: 22 }
                ScrollView {
                    id: steamScroll
                    Layout.fillWidth: true; Layout.fillHeight: true; clip: true
                    contentWidth: availableWidth
                    ColumnLayout {
                        width: steamScroll.availableWidth
                        spacing: 14
                        Label { text: window.state.steamDownloading ? "Downloading build" : "Sign in with Steam"; font.pixelSize: 28 }
                        Caption { Layout.fillWidth: true; wrapMode: Text.Wrap; text: "Sign in with your account, or scan the QR code with Steam Mobile." }
                        GridLayout {
                            visible: !window.state.steamDownloading; Layout.fillWidth: true; columns: steamScroll.availableWidth < 550 ? 1 : 2; columnSpacing: 32; rowSpacing: 20
                            ColumnLayout {
                                Layout.fillWidth: true; Layout.minimumWidth: 180; spacing: 8
                                Label { text: "Username" }
                                TextField { id: steamUsername; Layout.fillWidth: true; placeholderText: "Steam username"; enabled: window.state.steamProgress === 0 }
                                Label { text: "Password" }
                                TextField { id: steamPassword; Layout.fillWidth: true; placeholderText: "Password"; echoMode: TextInput.Password; enabled: window.state.steamProgress === 0 }
                                Button {
                                    text: "Log in"; highlighted: true; Layout.fillWidth: true
                                    enabled: steamUsername.text.length > 0 && steamPassword.text.length > 0 && window.state.steamProgress === 0
                                    onClicked: { backend.loginWithPassword(steamUsername.text, steamPassword.text); steamPassword.clear() }
                                }
                            }
                            ColumnLayout {
                                Layout.alignment: Qt.AlignHCenter; spacing: 10
                                Rectangle {
                                    Layout.alignment: Qt.AlignHCenter; Layout.preferredWidth: 220; Layout.preferredHeight: 220; color: "white"; radius: 8
                                    visible: !!window.state.steamQr
                                    Image { id: steamQrImage; anchors.fill: parent; anchors.margins: 8; source: window.state.steamQr || ""; smooth: false; mipmap: false; fillMode: Image.PreserveAspectFit }
                                }
                                BusyIndicator { Layout.alignment: Qt.AlignHCenter; visible: !window.state.steamQr; running: visible }
                                Caption { Layout.alignment: Qt.AlignHCenter; text: "Scan with the Steam Mobile App" }
                            }
                        }
                        Label { Layout.fillWidth: true; visible: !!window.state.steamPrompt; text: window.state.steamPrompt || ""; wrapMode: Text.Wrap }
                        RowLayout {
                            Layout.fillWidth: true; visible: !!window.state.steamPrompt
                            TextField { id: steamCode; Layout.fillWidth: true; placeholderText: "Steam Guard code"; echoMode: TextInput.Password; onAccepted: { backend.submitSteamCode(text); clear() } }
                            Button { text: "Submit"; onClicked: { backend.submitSteamCode(steamCode.text); steamCode.clear() } }
                        }
                    }
                }
                ProgressBar { Layout.fillWidth: true; from: 0; to: 100; value: window.state.steamProgress || 0; indeterminate: value === 0 && !window.state.steamQr }
                RowLayout {
                    Layout.fillWidth: true
                    Caption { Layout.fillWidth: true; wrapMode: Text.Wrap; text: window.state.status }
                    Button { text: "Cancel"; flat: true; onClicked: backend.cancel() }
                }
            }
        }
        Item {
            id: operationPage
            ColumnLayout {
                anchors.fill: parent; anchors.margins: 28; spacing: 16
                Label { text: window.state.steamDownloading ? "Downloading build" : "Preparing Gorilla Tag"; font.pixelSize: 28 }
                Rectangle {
                    Layout.fillWidth: true; Layout.fillHeight: true
                    color: "#222222"; radius: 8
                    ScrollView {
                        anchors.fill: parent; anchors.margins: 12; clip: true
                        TextArea {
                            id: operationLog
                            text: window.state.log || ""
                            readOnly: true; selectByMouse: true
                            wrapMode: TextEdit.WrapAnywhere
                            font.family: "Consolas"; font.pixelSize: 12
                            background: null
                            onTextChanged: cursorPosition = length
                        }
                    }
                }
                ProgressBar {
                    Layout.fillWidth: true; from: 0; to: 100
                    value: window.state.steamProgress || 0
                    indeterminate: !window.state.steamDownloading || value <= 0
                }
                RowLayout {
                    Layout.fillWidth: true
                    Caption { Layout.fillWidth: true; wrapMode: Text.Wrap; text: window.state.status }
                    Button { text: "Cancel"; flat: true; onClicked: backend.cancel() }
                }
            }
        }
    }
    DropArea {
        id: modDropArea
        anchors.fill: parent
        z: 100
        enabled: window.canManage && !modBrowser.visible && !deleteMod.visible && !updateDialog.visible
        onEntered: function(drag) { drag.accepted = window.canAcceptModDrop(drag.urls) }
        onDropped: function(drop) {
            if (window.installDroppedMod(drop.urls)) drop.accept(Qt.CopyAction)
            else drop.accepted = false
        }
        Pane {
            anchors.centerIn: parent
            visible: modDropArea.containsDrag
            padding: 24
            Material.elevation: 0
            Label { text: "Drop to install mod"; font.pixelSize: 20 }
        }
    }
    FileDialog {
        id: modBrowser
        title: "Install mod"
        nameFilters: ["BepInEx plugins (*.dll)"]
        onAccepted: backend.importMod(selectedFile.toString())
    }
    Dialog {
        id: deleteMod
        property string fileName: ""
        property string modName: ""
        anchors.centerIn: parent
        modal: true
        Overlay.modal: Rectangle { color: "#99000000" }
        title: "Delete mod?"
        standardButtons: Dialog.Cancel | Dialog.Ok
        Label { width: 320; text: "Remove " + deleteMod.modName + " from installed mods?"; wrapMode: Text.Wrap }
        onAccepted: { if (window.canManage) backend.removeMod(fileName) }
    }
    Dialog {
        id: updateDialog
        property string releaseVersion: ""
        property string releaseUrl: ""
        anchors.centerIn: parent
        width: Math.min(420, window.width - 48)
        modal: true
        Overlay.modal: Rectangle { color: "#99000000" }
        Material.elevation: 0
        title: "Update available"
        contentItem: Label {
            text: "PopeManager " + updateDialog.releaseVersion + " is available. You're running " + (window.state.appVersion || "") + "."
            wrapMode: Text.Wrap
        }
        footer: DialogButtonBox {
            Button { text: "Later"; flat: true; DialogButtonBox.buttonRole: DialogButtonBox.RejectRole }
            Button { text: "Update"; flat: true; DialogButtonBox.buttonRole: DialogButtonBox.AcceptRole }
        }
        onAccepted: Qt.openUrlExternally(releaseUrl)
    }
    ApplicationWindow {
        id: aboutWindow
        property string secretKeys: ""
        onVisibleChanged: {
            secretKeys = ""
            if (visible) aboutContent.forceActiveFocus()
        }
        onActiveChanged: { if (!active) secretKeys = "" }
        title: "About PopeManager"
        visible: false
        transientParent: window
        width: 440
        height: aboutContent.implicitHeight + 56
        minimumWidth: 400
        minimumHeight: aboutContent.implicitHeight + 56
        Material.theme: window.Material.theme
        font.family: window.font.family
        font.pixelSize: window.font.pixelSize
        ColumnLayout {
            id: aboutContent
            focus: true
            Keys.onPressed: function(event) {
                if (event.modifiers & (Qt.ControlModifier | Qt.AltModifier | Qt.MetaModifier)) {
                    aboutWindow.secretKeys = ""
                    return
                }
                if (event.key === Qt.Key_Backspace) {
                    aboutWindow.secretKeys = aboutWindow.secretKeys.slice(0, -1)
                    event.accepted = true
                    return
                }
                var letter = event.text.toLowerCase()
                if (!/^[a-z]$/.test(letter)) {
                    aboutWindow.secretKeys = ""
                    return
                }
                aboutWindow.secretKeys = (aboutWindow.secretKeys + letter).slice(-8)
                event.accepted = true
                if (aboutWindow.secretKeys === "solstice") {
                    aboutWindow.secretKeys = ""
                    puzzleSolved.stop()
                    puzzleSolved.play()
                    devWindow.show()
                    devWindow.raise()
                    devWindow.requestActivate()
                }
            }
            anchors.left: parent.left
            anchors.right: parent.right
            anchors.top: parent.top
            anchors.margins: 28
            spacing: 12
            Image { Layout.preferredWidth: 72; Layout.preferredHeight: 72; source: window.state.logoUrl || ""; fillMode: Image.PreserveAspectFit }
            Label { text: "PopeManager"; font.pixelSize: 26 }
            Label { text: "Version " + (window.state.appVersion || ""); opacity: 0.7 }
            Label { Layout.fillWidth: true; text: "Quest mod management by PopeMods."; wrapMode: Text.Wrap }
            Label { text: "PopeManager and related projects are licensed under AGPL-3.0"; opacity: 0.5; font.pixelSize: 8 }
            RowLayout {
                Layout.fillWidth: true
                Button { text: "GitHub"; flat: true; onClicked: Qt.openUrlExternally("https://github.com/PopeMods/PopeManager") }
                Button {
                    text: window.state.checkingUpdates ? "Checking..." : "Check for updates"
                    enabled: !window.state.checkingUpdates
                    onClicked: { window.offeredUpdate = ""; backend.checkForUpdates() }
                }
                Item { Layout.fillWidth: true }
                Button { text: "Close"; flat: true; onClicked: aboutWindow.close() }
            }
        }
    }
    ApplicationWindow {
        id: devWindow
        title: "Developer options"
        visible: false
        transientParent: window
        width: 400
        height: 220
        minimumWidth: 360
        minimumHeight: 200
        Material.theme: window.Material.theme
        font.family: window.font.family
        font.pixelSize: window.font.pixelSize
        ColumnLayout {
            anchors.fill: parent
            anchors.margins: 24
            spacing: 16
            Label { text: "Developer options"; font.pixelSize: 24 }
            Button { text: "Show update dialog"; onClicked: window.showUpdatePreview() }
            Item { Layout.fillHeight: true }
            Button { Layout.alignment: Qt.AlignRight; text: "Close"; flat: true; onClicked: devWindow.close() }
        }
    }
}
