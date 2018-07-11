﻿namespace GWallet.Frontend.XF

open System
open System.Threading.Tasks

open Xamarin.Forms
open Xamarin.Forms.Xaml

open Plugin.Clipboard
open ZXing
open ZXing.Net.Mobile.Forms
open ZXing.Common

open GWallet.Backend

type ReceivePage(account: NormalAccount,
                 accountBalance: Label,
                 fiatBalance: Label,
                 balancesPage: Page) as this =
    inherit ContentPage()
    let _ = base.LoadFromXaml(typeof<ReceivePage>)

    let baseAccount = account :> IAccount

    let mainLayout = base.FindByName<StackLayout>("mainLayout")
    do
        this.Init()

    member this.Init() =
        let balanceLabel = mainLayout.FindByName<Label>("balanceLabel")
        balanceLabel.Text <- accountBalance.Text
        balanceLabel.FontSize <- FrontendHelpers.BigFontSize
        let fiatBalanceLabel = mainLayout.FindByName<Label>("fiatBalanceLabel")
        fiatBalanceLabel.Text <- fiatBalance.Text
        fiatBalanceLabel.FontSize <- FrontendHelpers.MediumFontSize

        // FIXME: pass the decimal instead of doing the HACK below:
        if not (accountBalance.Text.StartsWith "0 ") then
            mainLayout.FindByName<Button>("sendButton").IsEnabled <- true

        let size = 200
        let encodingOptions = EncodingOptions(Height = size,
                                              Width = size)
        let barCode = ZXingBarcodeImageView(HorizontalOptions = LayoutOptions.Center,
                                            VerticalOptions = LayoutOptions.Center,
                                            BarcodeFormat = BarcodeFormat.QR_CODE,
                                            BarcodeValue = baseAccount.PublicAddress,
                                            HeightRequest = float size,
                                            WidthRequest = float size,
                                            BarcodeOptions = encodingOptions)
        mainLayout.Children.Add(barCode)

        let transactionHistoryButton = Button(Text = "View transaction history...")
        transactionHistoryButton.Clicked.Subscribe(fun _ ->
            Device.OpenUri (BlockExplorer.GetTransactionHistory baseAccount)
        ) |> ignore
        mainLayout.Children.Add(transactionHistoryButton)

        // FIXME: report this Xamarin.Forms Mac backend bug (no back button in navigation pages!, so below <workaround>)
        if (Device.RuntimePlatform <> Device.macOS) then () else

        let backButton = Button(Text = "< Go back")
        backButton.Clicked.Subscribe(fun _ ->
            balancesPage.Navigation.PopAsync() |> FrontendHelpers.DoubleCheckCompletion
        ) |> ignore
        mainLayout.Children.Add(backButton)
        //</workaround>

    member this.OnSendPaymentClicked(sender: Object, args: EventArgs) =
        let sendPage = SendPage(account, this)
        NavigationPage.SetHasNavigationBar(sendPage, false)
        let navSendPage = NavigationPage sendPage
        NavigationPage.SetHasNavigationBar(navSendPage, false)
        this.Navigation.PushAsync navSendPage
            |> FrontendHelpers.DoubleCheckCompletion
        ()

    member this.OnCopyToClipboardClicked(sender: Object, args: EventArgs) =
        let copyToClipboardButton = base.FindByName<Button>("copyToClipboardButton")
        FrontendHelpers.ChangeTextAndChangeBack copyToClipboardButton "Copied"

        CrossClipboard.Current.SetText baseAccount.PublicAddress
        ()
