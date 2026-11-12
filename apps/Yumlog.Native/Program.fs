namespace Yumlog.Native

open System
open System.Threading
open System.Windows.Forms

module Program =
    [<EntryPoint>]
    let main _ =
        let mutable createdNew = false
        use instanceMutex = new Mutex(true, @"Local\DarbotLabs.Yumlog.Native", &createdNew)
        if not createdNew then
            MessageBox.Show("Yumlog Native is already running.", "Yumlog Native", MessageBoxButtons.OK, MessageBoxIcon.Information)
            |> ignore
            0
        else
            try
                WinAppRuntime.tryInitialize() |> ignore
                NativeUi.run()
                WinAppRuntime.shutdown()
                0
            with ex ->
                WinAppRuntime.shutdown()
                MessageBox.Show(ex.Message, "Yumlog Native could not start", MessageBoxButtons.OK, MessageBoxIcon.Error)
                |> ignore
                1
