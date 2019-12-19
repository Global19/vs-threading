﻿/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Analyzers
{
    using System;
    using System.Collections.Immutable;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeActions;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Formatting;

    [ExportCodeFixProvider(LanguageNames.CSharp)]
    public class VSTHRD201CancelAfterSwitchToMainThreadCodeFix : CodeFixProvider
    {
        private static readonly ImmutableArray<string> ReusableFixableDiagnosticIds = ImmutableArray.Create(
            VSTHRD201CancelAfterSwitchToMainThreadAnalyzer.Id);

        /// <inheritdoc />
        public override ImmutableArray<string> FixableDiagnosticIds => ReusableFixableDiagnosticIds;

        /// <inheritdoc />
        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        /// <inheritdoc />
        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.First();

            // Check that the analyzer was able to give us some data we require.
            if (!diagnostic.Properties.ContainsKey(VSTHRD201CancelAfterSwitchToMainThreadAnalyzer.CancellationTokenNamePropertyName))
            {
                return;
            }

            // Our fix only works if we're within a block or if statement (no simple lambdas),
            // so check applicability before offering.
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var statementSyntax = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<StatementSyntax>();
            if (statementSyntax?.Parent is BlockSyntax || statementSyntax?.Parent is IfStatementSyntax)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Strings.VSTHRD201_CodeFix_Title,
                        ct => this.AddThrowOnCanceledAsync(context, diagnostic, ct),
                        equivalenceKey: nameof(CancellationToken.ThrowIfCancellationRequested)),
                    diagnostic);
            }
        }

        private async Task<Document> AddThrowOnCanceledAsync(CodeFixContext context, Diagnostic diagnostic, CancellationToken cancellationToken)
        {
            var semanticModel = await context.Document.GetSemanticModelAsync(cancellationToken);
            var root = await context.Document.GetSyntaxRootAsync(cancellationToken);
            var invocationSyntax = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<InvocationExpressionSyntax>();
            int argIndex = int.Parse(diagnostic.Properties[VSTHRD201CancelAfterSwitchToMainThreadAnalyzer.CancellationTokenNamePropertyName], CultureInfo.InvariantCulture);
            var tokenExpressionSyntax = invocationSyntax.ArgumentList.Arguments[argIndex].Expression;
            var statementSyntax = invocationSyntax.FirstAncestorOrSelf<StatementSyntax>();

            var checkTokenStatement = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        tokenExpressionSyntax,
                        SyntaxFactory.IdentifierName(nameof(CancellationToken.ThrowIfCancellationRequested)))))
                .WithAdditionalAnnotations(Formatter.Annotation);

            SyntaxNode updatedRoot;
            if (statementSyntax?.Parent is BlockSyntax containingBlock)
            {
                updatedRoot = root.ReplaceNode(
                    containingBlock,
                    containingBlock.InsertNodesAfter(statementSyntax, new[] { checkTokenStatement }));
            }
            else if (statementSyntax?.Parent is IfStatementSyntax ifStatement)
            {
                updatedRoot = root.ReplaceNode(
                    ifStatement,
                    ifStatement.WithStatement(
                        SyntaxFactory.Block(
                            statementSyntax,
                            checkTokenStatement)));
            }
            else
            {
                throw new NotSupportedException();
            }

            return context.Document.WithSyntaxRoot(updatedRoot);
        }
    }
}