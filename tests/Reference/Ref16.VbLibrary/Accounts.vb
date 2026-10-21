Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks

Namespace Accounts

    Public Enum AccountKind
        Checking
        Savings
    End Enum

    Public Class InsufficientFundsException
        Inherits Exception

        Public Sub New(needed As Decimal)
            MyBase.New("insufficient funds: need " & needed.ToString())
            Me.Needed = needed
        End Sub

        Public ReadOnly Property Needed As Decimal
    End Class

    Public Class Account
        Private _balance As Decimal
        Private ReadOnly _history As New List(Of String)

        Public Event BalanceChanged(sender As Object, newBalance As Decimal)

        Public Sub New(owner As String, kind As AccountKind)
            Me.Owner = owner
            Me.Kind = kind
        End Sub

        Public ReadOnly Property Owner As String
        Public ReadOnly Property Kind As AccountKind

        Public ReadOnly Property Balance As Decimal
            Get
                Return _balance
            End Get
        End Property

        Public Overridable Sub Deposit(amount As Decimal)
            If amount <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(amount))
            _balance += amount
            _history.Add("deposit " & amount.ToString())
            RaiseEvent BalanceChanged(Me, _balance)
        End Sub

        Public Overridable Sub Withdraw(amount As Decimal)
            If amount > _balance Then Throw New InsufficientFundsException(amount - _balance)
            _balance -= amount
            _history.Add("withdraw " & amount.ToString())
            RaiseEvent BalanceChanged(Me, _balance)
        End Sub

        Public Function History() As IEnumerable(Of String)
            Return _history.AsReadOnly()
        End Function

        Public Shared Operator +(a As Account, amount As Decimal) As Account
            a.Deposit(amount)
            Return a
        End Operator
    End Class

    Public NotInheritable Class SavingsAccount
        Inherits Account

        Public Sub New(owner As String)
            MyBase.New(owner, AccountKind.Savings)
        End Sub

        Public Property Rate As Decimal = 0.02D

        Public Overrides Sub Withdraw(amount As Decimal)
            If amount > 1000D Then Throw New InvalidOperationException("limit exceeded")
            MyBase.Withdraw(amount)
        End Sub

        Public Sub AddInterest()
            Deposit(Math.Round(Balance * Rate, 2))
        End Sub
    End Class

    Public Module Bank
        Public Function Describe(kind As AccountKind) As String
            Select Case kind
                Case AccountKind.Checking
                    Return "checking"
                Case AccountKind.Savings
                    Return "savings"
                Case Else
                    Return "unknown"
            End Select
        End Function

        Public Function Grade(score As Integer) As String
            Select Case score
                Case Is >= 90 : Return "A"
                Case 80 To 89 : Return "B"
                Case 70 To 79 : Return "C"
                Case Else : Return "F"
            End Select
        End Function

        Public Function RichOwners(accounts As IEnumerable(Of Account), limit As Decimal) As List(Of String)
            Return (From a In accounts
                    Where a.Balance > limit
                    Order By a.Owner
                    Select a.Owner).ToList()
        End Function

        Public Function TotalBy(accounts As IEnumerable(Of Account)) As Dictionary(Of AccountKind, Decimal)
            Return accounts.GroupBy(Function(a) a.Kind).ToDictionary(Function(g) g.Key, Function(g) g.Sum(Function(a) a.Balance))
        End Function

        Public Iterator Function Statements(account As Account) As IEnumerable(Of String)
            For Each line In account.History()
                Yield account.Owner & ": " & line
            Next
        End Function

        Public Async Function TransferAsync(source As Account, target As Account, amount As Decimal) As Task(Of Boolean)
            Await Task.Yield()
            Try
                source.Withdraw(amount)
                target.Deposit(amount)
                Return True
            Catch ex As InsufficientFundsException When ex.Needed < 10D
                Return False
            End Try
        End Function

        Public Function LegacyText(value As Object) As String
            Dim text As String = CStr(value)
            If Len(text) = 0 Then Return "(empty)"
            Return UCase(Left(text, 1)) & Mid(text, 2)
        End Function

        Public Function SafeDivide(a As Integer, b As Integer) As Integer
            Try
                Return a \ b
            Catch ex As DivideByZeroException
                Return 0
            Finally
                Console.WriteLine("divided")
            End Try
        End Function
    End Module

End Namespace
