Imports System.IO
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options

Namespace VbLib

    Public Interface IVbStore
        Function Describe() As String
        Sub Touch(name As String)
        Function ReadAsync(name As String) As Task(Of String)
    End Interface

    Public Class VbOptions
        Public Property RootPath As String = "/tmp"
    End Class

    Public Class VbStore
        Implements IVbStore

        Private ReadOnly _root As String

        Public Sub New(options As IOptions(Of VbOptions), logger As ILogger(Of VbStore))
            _root = options.Value.RootPath
        End Sub

        Public Function Describe() As String Implements IVbStore.Describe
            Return "VB store rooted at " & _root
        End Function

        Public Sub Touch(name As String) Implements IVbStore.Touch
            File.WriteAllText(Path.Combine(_root, name), "touched by VB")
        End Sub

        Public Async Function ReadAsync(name As String) As Task(Of String) Implements IVbStore.ReadAsync
            Return Await File.ReadAllTextAsync(Path.Combine(_root, name))
        End Function

    End Class

End Namespace
