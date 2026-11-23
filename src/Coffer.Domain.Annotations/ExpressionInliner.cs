using System.Linq.Expressions;
using System.Reflection;

namespace Coffer.Domain.Annotations;

/// <summary>
/// Composes one rule out of named parts without leaving a call in the tree that a
/// query provider cannot translate.
/// </summary>
/// <remarks>
/// <para>Inside an expression lambda, <c>Part.Inline(f)</c> stands for "the body of
/// <c>Part</c>, with <c>f</c> in place of its parameter". <see cref="Expand{TDelegate}"/>
/// performs that substitution, recursively, so the result is one plain tree: EF
/// translates it, and <c>Compile()</c> runs it in memory with the same meaning.</para>
/// <para>In-house rather than a package: it is the whole feature, and
/// a dependency here would be the shared project's only one.</para>
/// </remarks>
public static class ExpressionInliner
{
    /// <summary>
    /// Marker for <see cref="Expand{TDelegate}"/>. Never executes: a tree that still
    /// calls it was not expanded, and running it would silently skip the rule.
    /// </summary>
    public static TResult Inline<T, TResult>(this Expression<Func<T, TResult>> part, T argument) =>
        throw new InvalidOperationException(
            "Inline is a marker for ExpressionInliner.Expand; expand the expression before compiling or translating it.");

    /// <summary>The expression with every <see cref="Inline{T, TResult}"/> call replaced by its body.</summary>
    public static Expression<TDelegate> Expand<TDelegate>(this Expression<TDelegate> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return (Expression<TDelegate>)new InlineVisitor().Visit(expression);
    }

    private sealed class InlineVisitor : ExpressionVisitor
    {
        private static readonly MethodInfo Marker =
            typeof(ExpressionInliner).GetMethod(nameof(Inline))!;

        // A part over a small input type is inlined with a member-init argument, which
        // leaves `new T { A = x }.A` behind. The value it was given is the same
        // expression without the object; a query provider translates that, and in
        // memory it skips building an object only to read it back.
        protected override Expression VisitMember(MemberExpression node)
        {
            var inner = Visit(node.Expression);
            if (inner is MemberInitExpression init
                && init.Bindings.OfType<MemberAssignment>().FirstOrDefault(b => b.Member == node.Member) is { } assigned)
                return assigned.Expression;
            return node.Update(inner);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (!node.Method.IsGenericMethod || node.Method.GetGenericMethodDefinition() != Marker)
                return base.VisitMethodCall(node);

            var part = (LambdaExpression)Evaluate(node.Arguments[0]);
            var argument = Visit(node.Arguments[1]);
            var body = new ParameterReplacer(part.Parameters[0], argument).Visit(part.Body);
            // The part may itself inline other parts.
            return Visit(body);
        }

        // The part is a static field or property in practice; anything else is still
        // a closed expression and evaluates the same way.
        private static object Evaluate(Expression expression) => expression switch
        {
            MemberExpression { Expression: null, Member: FieldInfo field } => field.GetValue(null)!,
            MemberExpression { Expression: null, Member: PropertyInfo property } => property.GetValue(null)!,
            _ => Expression.Lambda<Func<object>>(Expression.Convert(expression, typeof(object))).Compile()(),
        };
    }

    private sealed class ParameterReplacer(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter ? replacement : base.VisitParameter(node);
    }
}
